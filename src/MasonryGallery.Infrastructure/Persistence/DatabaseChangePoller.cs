namespace MasonryGallery.Infrastructure.Persistence;

using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

/// <summary>
/// Notices commits made by another process, such as the main window's scans, for a viewer window that does no
/// indexing of its own. SQLite's <c>PRAGMA data_version</c> changes on a connection whenever any other connection
/// commits, so polling it on one long-lived connection is nearly free and needs no change to the schema.
/// </summary>
public sealed class DatabaseChangePoller : IDisposable
{
    private readonly string _dbPath;
    private readonly Action _onChange;
    private readonly TimeSpan _interval;
    private readonly ILogger<DatabaseChangePoller> _logger;
    private readonly CancellationTokenSource _stop = new();

    public DatabaseChangePoller(string dbPath, Action onChange, TimeSpan interval, ILogger<DatabaseChangePoller> logger)
    {
        _dbPath = dbPath;
        _onChange = onChange;
        _interval = interval;
        _logger = logger;
    }

    public void Start() => _ = RunAsync(_stop.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Private cache: in shared-cache mode, commits by connections sharing the cache don't move data_version.
            await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private
            }.ToString());
            await conn.OpenAsync(ct);
            var last = await conn.ExecuteScalarAsync<long>("PRAGMA data_version");

            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = await conn.ExecuteScalarAsync<long>("PRAGMA data_version");
                if (now == last)
                    continue;
                last = now;
                _onChange();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            // Without the poller the window still works; it just stops showing new files until reopened.
            _logger.LogWarning(ex, "Stopped watching the index for changes");
        }
    }

    public void Dispose() => _stop.Cancel();
}
