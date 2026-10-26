namespace MasonryGallery.Infrastructure.Tests;

using Dapper;
using FluentAssertions;
using MasonryGallery.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>What a viewer window relies on: knowing the schema is ready, and noticing the main window's writes.</summary>
public sealed class ViewerWindowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mg-viewer-").FullName;
    private string DbPath => Path.Combine(_dir, "t.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Schema_version_is_read_without_migrating()
    {
        var factory = new SqliteConnectionFactory(DbPath, NullLogger<SqliteConnectionFactory>.Instance);

        (await SchemaMigrator.GetVersionAsync(factory)).Should().Be(0);
        (await SchemaMigrator.GetVersionAsync(factory)).Should().Be(0, "reading the version must not create the schema");

        await SchemaMigrator.MigrateAsync(factory, NullLogger.Instance);
        (await SchemaMigrator.GetVersionAsync(factory)).Should().Be(SchemaMigrator.LatestVersion);
    }

    [Fact]
    public async Task Poller_reports_commits_from_other_connections_and_stays_quiet_otherwise()
    {
        var factory = new SqliteConnectionFactory(DbPath, NullLogger<SqliteConnectionFactory>.Instance);
        await SchemaMigrator.MigrateAsync(factory, NullLogger.Instance);
        var changes = 0;
        using var poller = new DatabaseChangePoller(DbPath, () => Interlocked.Increment(ref changes),
            TimeSpan.FromMilliseconds(50), NullLogger<DatabaseChangePoller>.Instance);
        poller.Start();

        await Task.Delay(300);
        changes.Should().Be(0);

        // Stands in for the main window's scan, which runs in another process.
        using (var other = factory.CreateConnection())
            await other.ExecuteAsync("INSERT INTO Library (Name, RootPath) VALUES ('x', '/x')");

        await MonitorWait(() => Volatile.Read(ref changes) > 0);
        await Task.Delay(300);
        changes.Should().Be(1, "one commit, one refresh");
    }

    private static async Task MonitorWait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }
}
