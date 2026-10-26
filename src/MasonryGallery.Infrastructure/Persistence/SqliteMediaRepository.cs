namespace MasonryGallery.Infrastructure.Persistence;

using System.Collections.Frozen;
using Dapper;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

public sealed class SqliteMediaRepository : IMediaRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<SqliteMediaRepository> _logger;

    public SqliteMediaRepository(SqliteConnectionFactory factory, ILogger<SqliteMediaRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // Every SET expression reads the pre-update row, so "changed" compares against the stored size/mtime.
    // A row that reappears on disk is revived (DeletedAt cleared), keeping its tags and favorite.
    // Scan rows carry mtime as a provisional DateTaken; only a probe result (ProbeState 1) may replace a stored one.
    private const string UpsertSql = @"
        INSERT INTO Media (LibraryId, RelPath, FileName, Kind, SizeBytes, ModifiedUtc,
                           DateTaken, DateAdded, Width, Height, DurationMs, Playable,
                           ProbeState, ThumbState, ThumbVersion, ScanId, Favorite, SidecarPath, SidecarModifiedUtc)
        VALUES (@LibraryId, @RelPath, @FileName, @Kind, @SizeBytes, @ModifiedUtc,
                @DateTaken, @DateAdded, @Width, @Height, @DurationMs, @Playable,
                @ProbeState, @ThumbState, @ThumbVersion, @ScanId, @Favorite, @SidecarPath, @SidecarModifiedUtc)
        ON CONFLICT(LibraryId, RelPath) DO UPDATE SET
            FileName = excluded.FileName,
            Kind = excluded.Kind,
            Width = COALESCE(excluded.Width, Media.Width),
            Height = COALESCE(excluded.Height, Media.Height),
            DurationMs = COALESCE(excluded.DurationMs, Media.DurationMs),
            Playable = COALESCE(excluded.Playable, Media.Playable),
            DateTaken = CASE WHEN excluded.ProbeState = 1 THEN COALESCE(excluded.DateTaken, Media.DateTaken)
                             ELSE COALESCE(Media.DateTaken, excluded.DateTaken) END,
            ProbeState = CASE WHEN Media.ModifiedUtc != excluded.ModifiedUtc OR Media.SizeBytes != excluded.SizeBytes
                              THEN excluded.ProbeState ELSE MAX(excluded.ProbeState, Media.ProbeState) END,
            ThumbState = CASE WHEN Media.ModifiedUtc != excluded.ModifiedUtc OR Media.SizeBytes != excluded.SizeBytes
                              THEN 0 ELSE Media.ThumbState END,
            -- Only a probe (which reads the sidecar) records the sidecar as read; a crash before that re-reads it.
            SidecarPath = CASE WHEN excluded.ProbeState != 0 THEN excluded.SidecarPath ELSE Media.SidecarPath END,
            SidecarModifiedUtc = CASE WHEN excluded.ProbeState != 0 THEN excluded.SidecarModifiedUtc ELSE Media.SidecarModifiedUtc END,
            ScanId = excluded.ScanId,
            SizeBytes = excluded.SizeBytes,
            ModifiedUtc = excluded.ModifiedUtc,
            DeletedAt = NULL";

    public Task<Result<int>> UpsertAsync(MediaItem item, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        conn.Execute(UpsertSql, item);
        return Task.FromResult(Result<int>.Success(item.Id));
    }

    public async Task<int> UpsertBatchAsync(IEnumerable<MediaItem> items, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        using var trans = conn.BeginTransaction();
        var count = await conn.ExecuteAsync(UpsertSql, items, transaction: trans);
        await trans.CommitAsync();
        return count;
    }

    public async Task<(ReadOnlyCollection<MediaItem> Items, bool HasMore)> QueryPageAsync(
        int limit,
        DateTime? lastDateTaken,
        int? lastId,
        LibraryId? libraryId,
        MediaKind? kind,
        string? tag,
        string? search,
        CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = new StringBuilder(@"
            SELECT m.Id, m.LibraryId, m.RelPath, m.FileName, m.Kind, m.SizeBytes,
                   m.ModifiedUtc, m.DateTaken, m.DateAdded,
                   m.Width, m.Height, m.DurationMs, m.ProbeState, m.ThumbState,
                   m.ThumbVersion, m.Favorite, m.DeletedAt
            FROM Media m
            WHERE m.DeletedAt IS NULL");

        var args = new DynamicParameters();

        if (libraryId.HasValue)
        {
            sql.Append(" AND m.LibraryId = @LibraryId");
            args.Add("LibraryId", libraryId.Value.Value);
        }

        if (kind.HasValue)
        {
            sql.Append(" AND m.Kind = @Kind");
            args.Add("Kind", kind.Value);
        }

        if (tag != null)
        {
            sql.Append(" AND m.Id IN (SELECT MediaId FROM MediaTag mt JOIN Tag t ON mt.TagId = t.Id WHERE t.Name = @Tag)");
            args.Add("Tag", tag);
        }

        AppendSearch(sql, args, search);

        if (lastDateTaken.HasValue && lastId.HasValue)
        {
            sql.Append(" AND (m.DateTaken, m.Id) < (@LastDateTaken, @LastId)");
            args.Add("LastDateTaken", new DateTimeOffset(lastDateTaken.Value).ToUnixTimeMilliseconds());
            args.Add("LastId", lastId.Value);
        }

        sql.Append(" ORDER BY m.DateTaken DESC, m.Id DESC LIMIT @Limit");
        args.Add("Limit", limit);

        var items = (await conn.QueryAsync<MediaItem>(sql.ToString(), args)).ToList();
        var hasMore = items.Count >= limit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);

        return (items.AsReadOnly(), hasMore);
    }

    // User text becomes quoted prefix terms ("cat"* "beach"*), so FTS operators and stray quotes can't cause syntax errors.
    /// <summary>
    /// Every search term must match, anywhere in the relative path (folders and file name, any case).
    /// Terms of 3+ characters use the trigram index, so "kpop" finds "IdiotsKPOP/…"; shorter ones (two-syllable
    /// Korean names, say) can't be trigram-matched and fall back to matching the start of a word.
    /// </summary>
    private static void AppendSearch(StringBuilder sql, DynamicParameters args, string? search)
    {
        var terms = (search ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < terms.Length; i++)
        {
            var quoted = "\"" + terms[i].Replace("\"", "\"\"") + "\"";
            if (terms[i].EnumerateRunes().Count() >= 3)
            {
                sql.Append($" AND m.Id IN (SELECT rowid FROM MediaPathFts WHERE MediaPathFts MATCH @Search{i})");
                args.Add($"Search{i}", quoted);
            }
            else
            {
                sql.Append($" AND m.Id IN (SELECT rowid FROM MediaFts WHERE MediaFts MATCH @Search{i})");
                args.Add($"Search{i}", quoted + "*");
            }
        }
    }

    public async Task<MediaItem?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<MediaItem>(
            "SELECT * FROM Media WHERE Id = @Id AND DeletedAt IS NULL", new { Id = id });
    }

    public Task<ReadOnlyCollection<MediaItem>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        if (!idList.Any())
            return Task.FromResult(ReadOnlyCollection<MediaItem>.Empty);

        using var conn = _factory.CreateConnection();
        var sql = $"SELECT * FROM Media WHERE Id IN ({string.Join(",", idList)}) AND DeletedAt IS NULL";
        var items = conn.Query<MediaItem>(sql).ToList();
        return Task.FromResult(items.AsReadOnly());
    }

    public async Task<IReadOnlyList<LibraryItem>> GetLayoutItemsAsync(MediaFilter filter, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = new StringBuilder(@"
            SELECT m.Id, m.Width, m.Height, m.Kind, m.ThumbState, m.Placeholder, m.Favorite
            FROM Media m
            WHERE m.DeletedAt IS NULL
              AND m.LibraryId IN (SELECT Id FROM Library WHERE Enabled = 1)");
        var args = new DynamicParameters();

        if (filter.LibraryId is { } libraryId)
        {
            sql.Append(" AND m.LibraryId = @LibraryId");
            args.Add("LibraryId", libraryId.Value);
        }
        if (filter.Kind is { } kind)
        {
            sql.Append(" AND m.Kind = @Kind");
            args.Add("Kind", (int)kind);
        }
        AppendSearch(sql, args, filter.Search);
        // One indexed lookup per tag (IX_MediaTag_Tag); Tag.Name compares without case.
        foreach (var (tag, i) in (filter.Tags ?? []).Select((t, i) => (t.Trim(), i)))
        {
            sql.Append($" AND m.Id IN (SELECT MediaId FROM MediaTag WHERE TagId = (SELECT Id FROM Tag WHERE Name = @Tag{i}))");
            args.Add($"Tag{i}", tag);
        }
        foreach (var (tag, i) in (filter.ExcludedTags ?? []).Select((t, i) => (t.Trim(), i)))
        {
            sql.Append($" AND m.Id NOT IN (SELECT MediaId FROM MediaTag WHERE TagId = (SELECT Id FROM Tag WHERE Name = @NotTag{i}))");
            args.Add($"NotTag{i}", tag);
        }
        if (filter.From is { } from)
        {
            sql.Append(" AND m.DateTaken >= @From");
            args.Add("From", new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
        }
        if (filter.To is { } to)
        {
            sql.Append(" AND m.DateTaken <= @To");
            args.Add("To", new DateTimeOffset(DateTime.SpecifyKind(to, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
        }
        if (filter.FavoritesOnly)
            sql.Append(" AND m.Favorite = 1");

        sql.Append(filter.Sort switch
        {
            MediaSort.OldestFirst => " ORDER BY m.DateTaken ASC, m.Id ASC",
            MediaSort.RecentlyAdded => " ORDER BY m.DateAdded DESC, m.Id DESC",
            MediaSort.Name => " ORDER BY m.FileName COLLATE NOCASE ASC, m.Id ASC",
            MediaSort.Random => "", // ordered below
            _ => " ORDER BY m.DateTaken DESC, m.Id DESC",
        });

        var rows = await conn.QueryAsync<(long Id, long? Width, long? Height, long Kind, long ThumbState, byte[]? Placeholder, long Favorite)>(
            sql.ToString(), args);
        // Unprobed items have no size yet; 1:1 keeps them in the grid until the probe reports in.
        var items = rows.Select(r => new LibraryItem((int)r.Id, (int)(r.Width ?? 1), (int)(r.Height ?? 1), (MediaKind)r.Kind,
            r.ThumbState == (int)ThumbState.Done, r.Placeholder, r.Favorite != 0));
        // Each item's place is a hash of its ID and the seed, not a shuffle of the list: refreshes during a scan
        // keep the order, and new items land at random spots instead of reshuffling everything.
        if (filter.Sort == MediaSort.Random)
            items = items.OrderBy(i => ShufflePosition(i.Id, filter.Seed)).ThenBy(i => i.Id);
        return items.ToList();
    }

    // SplitMix64 finalizer: consecutive IDs (files of one folder) end up far apart.
    internal static ulong ShufflePosition(int id, int seed)
    {
        var z = ((ulong)(uint)seed << 32 | (uint)id) + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public async Task<IReadOnlyDictionary<int, string>> GetFileNamesAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return new Dictionary<int, string>();
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<(long Id, string FileName)>(
            "SELECT Id, FileName FROM Media WHERE Id IN (SELECT value FROM json_each(@Ids))",
            new { Ids = JsonSerializer.Serialize(ids) });
        return rows.ToDictionary(r => (int)r.Id, r => r.FileName);
    }

    public async Task SetFavoriteAsync(int mediaId, bool favorite, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE Media SET Favorite = @Favorite WHERE Id = @Id",
            new { Id = mediaId, Favorite = favorite ? 1 : 0 });
    }

    public async Task<IReadOnlyList<TagCount>> ListTagsAsync(CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<(string Name, long Count, string? Category)>(
            @"SELECT t.Name, COUNT(*), t.Category FROM Tag t
              JOIN MediaTag mt ON mt.TagId = t.Id
              JOIN Media m ON m.Id = mt.MediaId
              WHERE m.DeletedAt IS NULL
              GROUP BY t.Id
              ORDER BY t.Name COLLATE NOCASE");
        return rows.Select(r => new TagCount(r.Name, (int)r.Count, r.Category)).ToList();
    }

    public async Task<IReadOnlyList<TagCount>> SearchTagsAsync(string prefix, int limit, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var escaped = prefix.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
        // Counted from MediaTag alone: joining Media for deleted rows would touch every tagged item per keystroke.
        var rows = await conn.QueryAsync<(string Name, long Count, string? Category)>(
            @"SELECT t.Name, COUNT(*) AS Uses, t.Category FROM Tag t
              JOIN MediaTag mt ON mt.TagId = t.Id
              WHERE t.Name LIKE @Prefix ESCAPE '\' OR t.Name LIKE @AfterUnderscore ESCAPE '\' OR t.Name LIKE @AfterSpace ESCAPE '\'
              GROUP BY t.Id
              ORDER BY t.Name LIKE @Prefix ESCAPE '\' DESC, Uses DESC, t.Name
              LIMIT @Limit",
            new { Prefix = escaped + "%", AfterUnderscore = @"%\_" + escaped + "%", AfterSpace = "% " + escaped + "%", Limit = limit });
        return rows.Select(r => new TagCount(r.Name, (int)r.Count, r.Category)).ToList();
    }

    public async Task SaveSidecarsAsync(
        int libraryId, IReadOnlyList<(string RelPath, SidecarData? Data)> sidecars, CancellationToken ct = default)
    {
        if (sidecars.Count == 0)
            return;
        using var conn = _factory.CreateConnection();
        using var trans = conn.BeginTransaction();

        var ids = (await conn.QueryAsync<(long Id, string RelPath)>(
                "SELECT Id, RelPath FROM Media WHERE LibraryId = @LibraryId AND RelPath IN (SELECT value FROM json_each(@Paths))",
                new { LibraryId = libraryId, Paths = JsonSerializer.Serialize(sidecars.Select(s => s.RelPath)) }, trans))
            .ToDictionary(r => r.RelPath, r => r.Id, StringComparer.Ordinal);
        var idsJson = JsonSerializer.Serialize(ids.Values);

        // Tags that may become unused once these items' sidecar tags are replaced.
        var previous = (await conn.QueryAsync<long>(
            "SELECT DISTINCT TagId FROM MediaTag WHERE Source = 1 AND MediaId IN (SELECT value FROM json_each(@Ids))",
            new { Ids = idsJson }, trans)).ToList();
        await conn.ExecuteAsync("DELETE FROM MediaTag WHERE Source = 1 AND MediaId IN (SELECT value FROM json_each(@Ids))",
            new { Ids = idsJson }, trans);
        await conn.ExecuteAsync("DELETE FROM Sidecar WHERE MediaId IN (SELECT value FROM json_each(@Ids))",
            new { Ids = idsJson }, trans);

        var present = sidecars.Where(s => s.Data is not null && ids.ContainsKey(s.RelPath)).ToList();
        var tags = present.SelectMany(s => s.Data!.Tags)
            .Where(t => Tag.TryCreate(t.Name).IsSuccess)
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { n = g.Key, c = g.Select(t => t.Category).FirstOrDefault(c => c is not null) })
            .ToList();
        if (tags.Count > 0)
        {
            // A category from any sidecar sticks; a sidecar without one doesn't erase it.
            await conn.ExecuteAsync(
                @"INSERT INTO Tag (Name, Category)
                  SELECT json_extract(value, '$.n'), json_extract(value, '$.c') FROM json_each(@Tags) WHERE true
                  ON CONFLICT(Name) DO UPDATE SET Category = COALESCE(excluded.Category, Tag.Category)",
                new { Tags = JsonSerializer.Serialize(tags) }, trans);
            var tagIds = (await conn.QueryAsync<(long Id, string Name)>(
                    "SELECT Id, Name FROM Tag WHERE Name IN (SELECT value FROM json_each(@Names))",
                    new { Names = JsonSerializer.Serialize(tags.Select(t => t.n)) }, trans))
                .ToDictionary(r => r.Name, r => r.Id, StringComparer.OrdinalIgnoreCase);
            var pairs = present.SelectMany(s => s.Data!.Tags
                    .Where(t => tagIds.ContainsKey(t.Name))
                    .Select(t => new[] { ids[s.RelPath], tagIds[t.Name] }))
                .ToList();
            // A tag the user already added by hand stays theirs (Source 0).
            await conn.ExecuteAsync(
                @"INSERT INTO MediaTag (MediaId, TagId, Source)
                  SELECT json_extract(value, '$[0]'), json_extract(value, '$[1]'), 1 FROM json_each(@Pairs) WHERE true
                  ON CONFLICT DO NOTHING",
                new { Pairs = JsonSerializer.Serialize(pairs) }, trans);
        }

        await conn.ExecuteAsync(
            @"INSERT INTO Sidecar (MediaId, Creator, Source, Title, Description, Rating)
              VALUES (@MediaId, @Creator, @Source, @Title, @Description, @Rating)",
            present.Select(s => new
            {
                MediaId = ids[s.RelPath], s.Data!.Creator, s.Data.Source, s.Data.Title, s.Data.Description, s.Data.Rating
            }), trans);

        if (previous.Count > 0)
            await conn.ExecuteAsync(
                @"DELETE FROM Tag WHERE Id IN (SELECT value FROM json_each(@Ids))
                  AND NOT EXISTS (SELECT 1 FROM MediaTag WHERE TagId = Tag.Id)",
                new { Ids = JsonSerializer.Serialize(previous) }, trans);

        await trans.CommitAsync();
    }

    public async Task<SidecarData?> GetSidecarAsync(int mediaId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<(string? Creator, string? Source, string? Title, string? Description, long? Rating)?>(
            "SELECT Creator, Source, Title, Description, Rating FROM Sidecar WHERE MediaId = @MediaId", new { MediaId = mediaId });
        return row is { } r ? new SidecarData([], r.Creator, r.Source, r.Title, r.Description, (int?)r.Rating) : null;
    }

    private const string FailedWhere = "m.DeletedAt IS NULL AND (m.ProbeState = 2 OR m.ThumbState = 2)";

    public async Task<IReadOnlyList<FailedItem>> GetFailedAsync(int limit, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<(long Id, string FileName, string RelPath, string LibraryName, long ProbeState)>(
            $@"SELECT m.Id, m.FileName, m.RelPath, l.Name, m.ProbeState
               FROM Media m JOIN Library l ON l.Id = m.LibraryId
               WHERE {FailedWhere}
               ORDER BY m.Id DESC LIMIT @Limit", new { Limit = limit });
        return rows.Select(r => new FailedItem((int)r.Id, r.FileName, r.RelPath, r.LibraryName,
                r.ProbeState == (int)ProbeState.Failed ? "probe" : "thumbnail"))
            .ToList();
    }

    public async Task<int> CountFailedAsync(CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        return await conn.QuerySingleAsync<int>($"SELECT COUNT(*) FROM Media m WHERE {FailedWhere}");
    }

    public async Task MarkScanCompleteAsync(int libraryId, int scanId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = @"
            UPDATE Media SET ScanId = @ScanId
            WHERE LibraryId = @LibraryId AND ScanId != @ScanId AND DeletedAt IS NULL";
        await conn.ExecuteAsync(sql, new { ScanId = scanId, LibraryId = libraryId });

        await SoftDeleteOldScansAsync(libraryId, scanId, 30, ct);
    }

    public async Task<int> SoftDeleteOldScansAsync(int libraryId, int scanId, int retentionDays, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        using var trans = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        await conn.ExecuteAsync(@"UPDATE Media SET DeletedAt = @Now
                                  WHERE LibraryId = @LibraryId AND ScanId != @ScanId AND DeletedAt IS NULL",
            new { Now = now.ToUnixTimeMilliseconds(), LibraryId = libraryId, ScanId = scanId }, trans);
        // Rows stay soft-deleted for the retention window so a file that comes back keeps its tags.
        var purged = await conn.ExecuteAsync("DELETE FROM Media WHERE LibraryId = @LibraryId AND DeletedAt < @Cutoff",
            new { LibraryId = libraryId, Cutoff = now.AddDays(-retentionDays).ToUnixTimeMilliseconds() }, trans);
        await trans.CommitAsync(ct);
        return purged;
    }

    // "Under folder F" is a prefix test on RelPath; substr avoids LIKE, where '%' and '_' in folder names would be wildcards.
    public async Task<int> SoftDeletePathsAsync(int libraryId, IReadOnlyCollection<string> relPaths, CancellationToken ct = default)
    {
        if (relPaths.Count == 0)
            return 0;
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteAsync(
            @"UPDATE Media SET DeletedAt = @Now
              WHERE LibraryId = @LibraryId AND DeletedAt IS NULL
                AND EXISTS (SELECT 1 FROM json_each(@Paths) p
                            WHERE Media.RelPath = p.value
                               OR substr(Media.RelPath, 1, length(p.value) + 1) = p.value || '/')",
            new { LibraryId = libraryId, Paths = JsonSerializer.Serialize(relPaths), Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    public async Task<int> SoftDeleteUnseenUnderAsync(int libraryId, string relDirectory, int scanId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteAsync(
            @"UPDATE Media SET DeletedAt = @Now
              WHERE LibraryId = @LibraryId AND DeletedAt IS NULL AND ScanId != @ScanId
                AND substr(RelPath, 1, length(@Dir) + 1) = @Dir || '/'",
            new { LibraryId = libraryId, Dir = relDirectory.Trim('/'), ScanId = scanId, Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    public async Task<IReadOnlySet<int>> GetAllIdsAsync(CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        return (await conn.QueryAsync<int>("SELECT Id FROM Media")).ToHashSet();
    }

    public async Task<int> CountLiveAsync(int libraryId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        return await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM Media WHERE LibraryId = @LibraryId AND DeletedAt IS NULL", new { LibraryId = libraryId });
    }

    public async Task<IReadOnlyDictionary<string, ScanState>> GetScanStatesAsync(
        int libraryId, IReadOnlyList<string> relPaths, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        // One JSON parameter instead of thousands of bind variables.
        var rows = await conn.QueryAsync<(string RelPath, long SizeBytes, long ModifiedUtc, int ProbeState, long? SidecarModifiedUtc)>(
            @"SELECT RelPath, SizeBytes, ModifiedUtc, ProbeState, SidecarModifiedUtc FROM Media
              WHERE LibraryId = @LibraryId AND RelPath IN (SELECT value FROM json_each(@Paths))",
            new { LibraryId = libraryId, Paths = JsonSerializer.Serialize(relPaths) });
        return rows.ToDictionary(
            r => r.RelPath,
            r => new ScanState(r.SizeBytes, DateTimeOffset.FromUnixTimeMilliseconds(r.ModifiedUtc).UtcDateTime, (ProbeState)r.ProbeState,
                r.SidecarModifiedUtc is { } sidecar ? DateTimeOffset.FromUnixTimeMilliseconds(sidecar).UtcDateTime : null),
            StringComparer.Ordinal);
    }

    public async Task<int> CountPendingThumbnailsAsync(int libraryId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = @"SELECT COUNT(*) FROM Media WHERE LibraryId = @LibraryId AND ThumbState = @State AND DeletedAt IS NULL";
        return await conn.QuerySingleAsync<int>(sql, new { LibraryId = libraryId, State = (int)ThumbState.Pending });
    }

    public async Task<int> ResetOutdatedThumbnailsAsync(int libraryId, int currentVersion, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteAsync(
            @"UPDATE Media SET ThumbState = 0
              WHERE LibraryId = @LibraryId AND ThumbState != 0 AND ThumbVersion < @Version",
            new { LibraryId = libraryId, Version = currentVersion });
    }

    public async Task<ReadOnlyCollection<MediaItem>> GetPendingThumbnailsAsync(
        int libraryId, int count, (DateTime DateTaken, int Id)? after, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        // Walks IX_Media_Library_Taken from the cursor on; DeletedAt IS NULL lets SQLite use that partial index.
        var sql = @"SELECT * FROM Media
                    WHERE LibraryId = @LibraryId AND ThumbState = 0 AND DeletedAt IS NULL"
                  + (after is null ? "" : " AND (DateTaken, Id) < (@AfterDate, @AfterId)")
                  + " ORDER BY DateTaken DESC, Id DESC LIMIT @Count";
        var items = await conn.QueryAsync<MediaItem>(sql, new
        {
            LibraryId = libraryId,
            Count = count,
            AfterDate = after?.DateTaken ?? default,
            AfterId = after?.Id ?? 0
        });
        return items.ToList().AsReadOnly();
    }

    public async Task<ReadOnlyCollection<MediaItem>> GetPendingThumbnailsByIdAsync(
        int libraryId, IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return ReadOnlyCollection<MediaItem>.Empty;

        using var conn = _factory.CreateConnection();
        var items = await conn.QueryAsync<MediaItem>(
            @"SELECT * FROM Media
              WHERE Id IN (SELECT value FROM json_each(@Ids))
                AND LibraryId = @LibraryId AND ThumbState = 0 AND DeletedAt IS NULL",
            new { LibraryId = libraryId, Ids = JsonSerializer.Serialize(ids) });
        return items.ToList().AsReadOnly();
    }

    public async Task UpdateThumbAsync(int mediaId, ThumbState state, int thumbVersion, int? averageRgb = null, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = @"UPDATE Media SET ThumbState = @State, ThumbVersion = @ThumbVersion,
                        Placeholder = COALESCE(@Placeholder, Placeholder)
                    WHERE Id = @Id";
        await conn.ExecuteAsync(sql, new
        {
            Id = mediaId,
            State = state,
            ThumbVersion = thumbVersion,
            Placeholder = averageRgb is { } rgb ? PlaceholderColor.ToBytes(rgb) : null
        });
    }

    public async Task<ReadOnlyCollection<Tag>> GetTagsForMediaAsync(int mediaId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = @"SELECT t.Name, t.Category, mt.Source FROM Tag t
                    JOIN MediaTag mt ON t.Id = mt.TagId
                    WHERE mt.MediaId = @MediaId
                    ORDER BY t.Name COLLATE NOCASE";
        var rows = await conn.QueryAsync<(string Name, string? Category, long Source)>(sql, new { MediaId = mediaId });
        return rows.Select(r => new Tag(r.Name) { Category = r.Category, FromSidecar = r.Source == 1 }).ToList().AsReadOnly();
    }

    public async Task SetTagsAsync(int mediaId, IEnumerable<string> tagNames, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        using var trans = conn.BeginTransaction();

        // Only the user's own tags; the sidecar's are replaced when the sidecar changes.
        await conn.ExecuteAsync("DELETE FROM MediaTag WHERE MediaId = @MediaId AND Source = 0", new { MediaId = mediaId }, trans);

        // Tag names are case-insensitive (Tag.Name COLLATE NOCASE); keep the first spelling of each.
        var names = tagNames.Select(n => n.Trim())
            .Where(n => Tag.TryCreate(n).IsSuccess)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            await conn.ExecuteAsync("INSERT INTO Tag (Name) VALUES (@Name) ON CONFLICT(Name) DO NOTHING", new { Name = name }, trans);
            var tagId = await conn.QuerySingleAsync<int>("SELECT Id FROM Tag WHERE Name = @Name", new { Name = name }, trans);
            await conn.ExecuteAsync("INSERT OR IGNORE INTO MediaTag (MediaId, TagId) VALUES (@MediaId, @TagId)",
                new { MediaId = mediaId, TagId = tagId }, trans);
        }
        // Drop tags nothing uses any more, so typos don't linger in the tag list.
        await conn.ExecuteAsync("DELETE FROM Tag WHERE Id NOT IN (SELECT TagId FROM MediaTag)", transaction: trans);

        await trans.CommitAsync();
    }

    public async Task UpsertTagAsync(string name, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = @"INSERT INTO Tag (Name) VALUES (@Name)
                    ON CONFLICT(Name) DO NOTHING";
        await conn.ExecuteAsync(sql, new { Name = name });
    }

    public async Task<int> GetTagIdAsync(string name, CancellationToken ct = default)
    {
        // Awaited, not returned: the connection must outlive the query.
        using var conn = _factory.CreateConnection();
        var sql = "SELECT Id FROM Tag WHERE Name = @Name";
        return await conn.QuerySingleAsync<int>(sql, new { Name = name });
    }

    public Task<ReadOnlyCollection<(int MediaId, string TagName)>> SearchByTagAsync(string tagName, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = @"SELECT MediaId, Name FROM MediaTag mt JOIN Tag t ON mt.TagId = t.Id WHERE t.Name = @TagName";
        var items = conn.Query<(int MediaId, string TagName)>(sql, new { TagName = tagName }).ToList();
        return Task.FromResult(items.AsReadOnly());
    }

    public async Task<bool> PathExistsAsync(LibraryId libraryId, string relPath, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = "SELECT COUNT(*) FROM Media WHERE LibraryId = @LibraryId AND RelPath = @RelPath AND DeletedAt IS NULL";
        var count = await conn.QuerySingleAsync<int>(sql, new { LibraryId = libraryId.Value, RelPath = relPath });
        return count > 0;
    }

    public Task<ReadOnlyCollection<MediaItem>> GetChangedSinceAsync(int libraryId, DateTime since, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = @"SELECT * FROM Media
                    WHERE LibraryId = @LibraryId AND ModifiedUtc > @Since AND DeletedAt IS NULL";
        var items = conn.Query<MediaItem>(sql, new { LibraryId = libraryId, Since = new DateTimeOffset(since).ToUnixTimeMilliseconds() }).ToList();
        return Task.FromResult(items.AsReadOnly());
    }
}
