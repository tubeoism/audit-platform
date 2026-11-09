using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuditPlatform.Master.Data.Tests;

/// <summary>
/// Creates a throw-away database on the server named by AUDIT_TEST_PG (an admin connection string), applies all
/// migrations, and drops it afterwards. Tests skip when the variable is not set, so the default test run needs no server.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Variable = "AUDIT_TEST_PG";
    private readonly string? _admin = Environment.GetEnvironmentVariable(Variable);
    private readonly string _database = "audit_test_" + Guid.NewGuid().ToString("N")[..12];

    public string? ConnectionString { get; private set; }

    public MasterDbContext CreateContext()
    {
        if (ConnectionString is null)
        {
            Assert.Skip($"Set {Variable} to a PostgreSQL admin connection string to run database tests.");
        }

        return new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention().Options);
    }

    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_admin))
        {
            return;
        }

        await using (var conn = new NpgsqlConnection(_admin))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {_database}", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(_admin) { Database = _database }.ConnectionString;
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_admin);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
