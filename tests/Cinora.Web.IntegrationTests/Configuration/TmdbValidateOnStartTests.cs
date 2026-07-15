using Cinora.Infrastructure.Options;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Serilog;

namespace Cinora.Web.IntegrationTests.Configuration;

/// <summary>
/// Verifies Milestone 2.0's fail-closed contract: with <c>TmdbOptions.ValidateOnStart()</c> now ON, a
/// blank <c>Tmdb:ApiKey</c> aborts application startup (an <see cref="OptionsValidationException"/>) rather
/// than surfacing on the first TMDB call. Runs in the shared, non-parallel collection so its throwaway host
/// does not race the other integration hosts over the LocalDB test database.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class TmdbValidateOnStartTests
{
    [Fact]
    public void App_fails_fast_at_startup_when_the_tmdb_api_key_is_blank()
    {
        // Program.cs's UseSerilog() reconfigures the process-global static Log.Logger while BUILDING the
        // host — which happens before ValidateOnStart throws at StartAsync. Building this throwaway host
        // would therefore replace the shared factory's request-logging sink and break the sibling
        // diagnostics tests in this (non-parallel) collection. Save and restore Log.Logger around it.
        var originalLogger = Log.Logger;
        try
        {
            using var factory = new BlankTmdbApiKeyFactory();

            // Building/starting the host (accessing Services) triggers ValidateOnStart, which fails on the
            // blank key before the app can serve a single request.
            var exception = Assert.Throws<OptionsValidationException>(() => _ = factory.Services);

            Assert.Contains(
                nameof(TmdbOptions.ApiKey),
                string.Join(" ", exception.Failures),
                StringComparison.Ordinal);
        }
        finally
        {
            Log.Logger = originalLogger;
        }
    }

    // A standalone host (CinoraWebApplicationFactory is sealed) that mirrors its Testing environment +
    // LocalDB connection string via process env vars set before the host is built, then overrides
    // Tmdb:ApiKey to blank via the highest-precedence configuration source. The in-memory source added here
    // wins in the fully-built configuration that BindConfiguration reads, so ValidateOnStart fails at boot
    // even though CinoraWebApplicationFactory injects a dummy key via an env var.
    private sealed class BlankTmdbApiKeyFactory : WebApplicationFactory<Program>
    {
        private const string TestConnectionString =
            "Server=(localdb)\\MSSQLLocalDB;Database=CinoraTest;Trusted_Connection=True;" +
            "MultipleActiveResultSets=true;TrustServerCertificate=True";

        static BlankTmdbApiKeyFactory()
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", TestConnectionString);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ConfigureAppConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Tmdb:ApiKey"] = string.Empty,
                }));
        }
    }
}
