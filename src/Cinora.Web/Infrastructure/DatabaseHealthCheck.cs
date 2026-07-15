using Cinora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cinora.Web.Infrastructure;

/// <summary>
/// Reports whether the application can reach its SQL Server database. The check calls
/// <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.CanConnectAsync"/> on the
/// <see cref="CinoraDbContext"/> — a lightweight connectivity probe that opens and closes a connection
/// without touching data — and maps success to <see cref="HealthStatus.Healthy"/> and failure to
/// <see cref="HealthStatus.Unhealthy"/>. It backs the anonymous <c>/health</c> endpoint (Milestone 1.7),
/// which therefore returns 200 when the database is reachable and 503 when it is not.
/// </summary>
/// <remarks>
/// Uses EF Core (already referenced by Cinora.Web) rather than a dedicated health-check package, so no new
/// NuGet dependency is introduced. <c>CanConnectAsync</c> swallows connection failures and returns
/// <see langword="false"/> rather than throwing; the health-check middleware also treats an unexpected throw
/// as unhealthy, so either way an unreachable database surfaces as 503.
/// </remarks>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly CinoraDbContext _dbContext;

    /// <summary>Initializes a new instance of the <see cref="DatabaseHealthCheck"/> class.</summary>
    /// <param name="dbContext">The EF Core context whose database connectivity is probed.</param>
    public DatabaseHealthCheck(CinoraDbContext dbContext) => _dbContext = dbContext;

    /// <summary>Probes database connectivity and returns the corresponding health result.</summary>
    /// <param name="context">The health-check context supplied by the middleware.</param>
    /// <param name="cancellationToken">A token to cancel the connectivity probe.</param>
    /// <returns>
    /// <see cref="HealthCheckResult.Healthy(string, System.Collections.Generic.IReadOnlyDictionary{string, object})"/>
    /// when the database is reachable; otherwise
    /// <see cref="HealthCheckResult.Unhealthy(string, System.Exception, System.Collections.Generic.IReadOnlyDictionary{string, object})"/>.
    /// </returns>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);

        return canConnect
            ? HealthCheckResult.Healthy("Database is reachable.")
            : HealthCheckResult.Unhealthy("Database is unreachable.");
    }
}
