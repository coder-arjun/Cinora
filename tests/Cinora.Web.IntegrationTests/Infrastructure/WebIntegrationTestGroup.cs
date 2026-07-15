namespace Cinora.Web.IntegrationTests.Infrastructure;

/// <summary>
/// Shares a single <see cref="CinoraWebApplicationFactory"/> across every integration test class that
/// boots the app, and disables parallelization so the classes run sequentially. This prevents two hosts
/// from concurrently rebuilding the shared <c>CinoraTest</c> LocalDB (a "database in use" race) — the
/// isolation contract recorded in REVIEW_BACKLOG for adding a second factory-using test class.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WebIntegrationTestGroup : ICollectionFixture<CinoraWebApplicationFactory>
{
    /// <summary>The collection name shared by all factory-using integration test classes.</summary>
    public const string Name = "Cinora Web integration tests";
}
