using Cinora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;

namespace Cinora.Web.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real <c>Cinora.Web</c> pipeline in-process for integration tests, pointed at a dedicated
/// LocalDB database (<c>CinoraTest</c>) so tests never touch developer data (ADR 0002 — Testcontainers
/// unavailable). The connection string and a non-Development environment are injected as process
/// environment variables in the static constructor, BEFORE <c>WebApplication.CreateBuilder</c> reads
/// configuration — this both satisfies the fail-fast connection-string check in <c>AddInfrastructure</c>
/// (which runs before any test-services override could apply) and keeps user-secrets/appsettings.Development
/// out of the picture, so Google OAuth stays deliberately unconfigured and tests are deterministic.
/// </summary>
public sealed class CinoraWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>The isolated LocalDB test database — deliberately NOT the <c>CinoraDev</c> developer DB.</summary>
    private const string TestConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=CinoraTest;Trusted_Connection=True;" +
        "MultipleActiveResultSets=true;TrustServerCertificate=True";

    // Serializes the one-time schema build and memoizes it as a single Task the tests await.
    private readonly object _databaseGate = new();
    private Task? _databaseReady;

    /// <summary>
    /// The in-memory Serilog sink wired into the running app's logger, so tests can assert that expected
    /// log events (e.g. the request-completion event) were produced.
    /// </summary>
    public InMemoryLogSink LogSink { get; } = new();

    static CinoraWebApplicationFactory()
    {
        // Set before the host is ever created so CreateBuilder's environment-variable configuration
        // source picks them up. "Testing" is not "Development", so no user-secrets are loaded and
        // Google OAuth is left unconfigured (deterministic external-provider assertions).
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", TestConnectionString);

        // Milestone 2.0: TmdbOptions is now ValidateOnStart, so the app fails to boot without a
        // Tmdb:ApiKey. Inject a dummy NON-SECRET key (the data annotation only checks non-empty) so the
        // whole integration suite still boots; a faked/absent network means no live TMDB call is made. The
        // blank-key fail-fast path is proven separately (TmdbValidateOnStartTests), which overrides this to
        // empty via a higher-precedence configuration source.
        Environment.SetEnvironmentVariable("Tmdb__ApiKey", "dummy-test-key");
    }

    /// <summary>
    /// Registers the in-memory Serilog sink in the test host's container. The Program's
    /// <c>UseSerilog(...).ReadFrom.Services(sp)</c> then adds every DI-registered <see cref="ILogEventSink"/>
    /// to the logger, so <see cref="LogSink"/> observes exactly what the app logs at runtime.
    /// </summary>
    /// <param name="builder">The web host builder being configured for the test server.</param>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureTestServices(services =>
            services.AddSingleton<ILogEventSink>(LogSink));
    }

    /// <summary>
    /// Ensures the <c>CinoraTest</c> database exists with the current migrated schema. Runs its work
    /// exactly once per factory instance (memoized); callers await it from each test's initialization so
    /// the schema is ready before the first request, while the actual delete+migrate happens only once.
    /// </summary>
    /// <returns>A task that completes when the migrated schema is in place.</returns>
    public Task EnsureDatabaseReadyAsync()
    {
        lock (_databaseGate)
        {
            return _databaseReady ??= BuildSchemaAsync();
        }
    }

    private async Task BuildSchemaAsync()
    {
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        // Clean slate, then apply migrations — a migrated schema (not EnsureCreated) so relational
        // constraints and value converters behave exactly as in production.
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.MigrateAsync();
    }
}
