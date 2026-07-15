using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Tests.Common.Fakes;

/// <summary>
/// Hands out <see cref="TestAppDbContext"/> instances that all share ONE isolated SQLite in-memory database.
/// A <c>DataSource=:memory:</c> database exists only for as long as a connection to it stays open, and each
/// connection gets its OWN private database — so this holds a single open <see cref="SqliteConnection"/> and
/// hands every context that same connection. Sharing lets a "concurrent" sibling context commit a raced row
/// that another context then reads (the mechanism the race-path tests rely on), while SQLite really enforces
/// the unique indexes the fixture depends on. A fresh instance per test keeps stores isolated across tests;
/// dispose it to drop the database and release the connection.
/// </summary>
internal sealed class SharedSqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Opens the shared in-memory connection and creates the catalog schema once against it.</summary>
    public SharedSqliteStore()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Create the tables + unique indexes once; the schema then lives as long as the connection is open.
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    /// <summary>Creates a new context over this store's single shared connection.</summary>
    public TestAppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TestAppDbContext>()
            .UseSqlite(_connection)
            .Options);

    /// <summary>Closes the shared connection, dropping the in-memory database.</summary>
    public void Dispose() => _connection.Dispose();
}
