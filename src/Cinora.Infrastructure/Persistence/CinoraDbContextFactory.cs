using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cinora.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by the <c>dotnet ef</c> tools to construct a <see cref="CinoraDbContext"/>
/// outside the application host (no DI, no <c>Program.cs</c>). It points at the same local SQL Server
/// instance the app uses in Development so <c>migrations add</c>/<c>database update</c> work reliably.
/// </summary>
public sealed class CinoraDbContextFactory : IDesignTimeDbContextFactory<CinoraDbContext>
{
    // WHY: the tools run without configuration binding; this local SQL Server connection string is safe
    // to hardcode here because it is a local, Windows-integrated-auth developer instance (ADR 0002).
    private const string DesignTimeConnectionString =
        "Server=localhost;Database=CinoraDev;Trusted_Connection=True;" +
        "MultipleActiveResultSets=true;TrustServerCertificate=True";

    /// <summary>Creates a <see cref="CinoraDbContext"/> for design-time tooling.</summary>
    /// <param name="args">Command-line arguments passed by the EF tools (unused).</param>
    /// <returns>A context configured against the local development database.</returns>
    public CinoraDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CinoraDbContext>()
            .UseSqlServer(
                DesignTimeConnectionString,
                sql => sql.MigrationsAssembly(typeof(CinoraDbContext).Assembly.FullName))
            .Options;

        return new CinoraDbContext(options);
    }
}
