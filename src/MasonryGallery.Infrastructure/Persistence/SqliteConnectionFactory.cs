namespace MasonryGallery.Infrastructure.Persistence;

using System.Data;
using Dapper;
using MasonryGallery.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

public sealed class SqliteConnectionFactory
{
    static SqliteConnectionFactory()
    {
        SqlMapper.AddTypeHandler(new LibraryIdHandler());
        SqlMapper.RemoveTypeMap(typeof(DateTime));
        SqlMapper.RemoveTypeMap(typeof(DateTime?));
        SqlMapper.AddTypeHandler(new UnixMsDateTimeHandler());
    }

    private readonly string _connectionString;
    private readonly ILogger<SqliteConnectionFactory> _logger;

    public SqliteConnectionFactory(string dbPath, ILogger<SqliteConnectionFactory> logger)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
        _logger = logger;
    }

    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        ConfigureConnection(conn);
        return conn;
    }

    public void ConfigureConnection(SqliteConnection conn)
    {
        conn.Execute("PRAGMA journal_mode=WAL");
        conn.Execute("PRAGMA synchronous=NORMAL");
        conn.Execute("PRAGMA foreign_keys=ON");
        conn.Execute("PRAGMA temp_store=MEMORY");
        conn.Execute("PRAGMA mmap_size=268435456");
        conn.Execute("PRAGMA busy_timeout=5000");
    }
}

internal sealed class LibraryIdHandler : SqlMapper.TypeHandler<LibraryId>
{
    public override void SetValue(IDbDataParameter parameter, LibraryId value)
    {
        parameter.DbType = DbType.Int32;
        parameter.Value = value.Value;
    }

    public override LibraryId Parse(object value) => new(Convert.ToInt32(value));
}

// DateTime columns are unix milliseconds (see 0001_init.sql).
internal sealed class UnixMsDateTimeHandler : SqlMapper.TypeHandler<DateTime>
{
    public override void SetValue(IDbDataParameter parameter, DateTime value)
    {
        parameter.DbType = DbType.Int64;
        parameter.Value = new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeMilliseconds();
    }

    public override DateTime Parse(object value)
        => DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(value)).UtcDateTime;
}
