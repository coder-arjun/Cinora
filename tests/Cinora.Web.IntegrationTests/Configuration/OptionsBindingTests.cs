using Cinora.Infrastructure.Options;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cinora.Web.IntegrationTests.Configuration;

/// <summary>
/// Verifies the free-provider options (<see cref="TmdbOptions"/>, <see cref="AiOptions"/>,
/// <see cref="CacheOptions"/>, <see cref="FileStorageOptions"/>) are bound at the composition root and
/// resolve with the expected non-secret defaults from <c>appsettings.json</c>. Milestone 2.0 flipped
/// <see cref="TmdbOptions"/> to <c>ValidateOnStart</c> (TMDB is now consumed), so the Testing host injects
/// a dummy non-secret <c>Tmdb:ApiKey</c> and TMDB now resolves successfully at boot; the blank-key
/// fail-fast path is proven in <c>TmdbValidateOnStartTests</c>. <see cref="AiOptions"/>,
/// <see cref="CacheOptions"/>, and <see cref="FileStorageOptions"/> remain lazily validated (not
/// <c>ValidateOnStart</c>). Shares the single factory via the non-parallel collection.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class OptionsBindingTests
{
    private static readonly string[] ExpectedImageContentTypes = ["image/jpeg", "image/png", "image/webp"];

    private readonly CinoraWebApplicationFactory _factory;

    public OptionsBindingTests(CinoraWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void TmdbOptions_binds_the_non_secret_defaults_and_the_injected_testing_api_key()
    {
        // Read the configuration the running app actually loaded. The non-secret defaults live in
        // appsettings.json; Milestone 2.0's ValidateOnStart means the Testing host injects a dummy
        // Tmdb:ApiKey (CinoraWebApplicationFactory) so the app boots without a live key.
        var configuration = _factory.Services.GetRequiredService<IConfiguration>();

        Assert.Equal("https://api.themoviedb.org/3", configuration["Tmdb:BaseUrl"]);
        Assert.Equal("https://image.tmdb.org/t/p", configuration["Tmdb:ImageBaseUrl"]);
        Assert.False(string.IsNullOrEmpty(configuration["Tmdb:ApiKey"]));
    }

    [Fact]
    public void TmdbOptions_resolves_successfully_at_boot_now_that_validate_on_start_is_on()
    {
        // Milestone 2.0 flipped TmdbOptions to ValidateOnStart. The host booting at all (this resolve
        // proves it) means the dummy Testing key satisfied validation eagerly — so .Value no longer throws
        // (contrast the Phase 1 lazy-validation behavior).
        var options = _factory.Services.GetRequiredService<IOptions<TmdbOptions>>();

        var value = options.Value;

        Assert.False(string.IsNullOrEmpty(value.ApiKey));
        Assert.Equal("https://api.themoviedb.org/3", value.BaseUrl);
        Assert.Equal("https://image.tmdb.org/t/p", value.ImageBaseUrl);
    }

    [Fact]
    public void AiOptions_binds_the_free_local_ollama_defaults()
    {
        var options = Resolve<AiOptions>();

        Assert.Equal("Ollama", options.Provider);
        Assert.Equal("http://localhost:11434/v1", options.Endpoint);
        Assert.Equal("llama3.2", options.Model);
        Assert.Equal(60, options.TimeoutSeconds);
        Assert.Equal(1024, options.MaxOutputTokens);
    }

    [Fact]
    public void CacheOptions_binds_the_in_memory_defaults()
    {
        var options = Resolve<CacheOptions>();

        Assert.Equal("Memory", options.Provider);
        Assert.Equal(300, options.DefaultTtlSeconds);

        // The optional Redis connection string is absent for the free in-memory default.
        Assert.Null(options.RedisConnectionString);
    }

    [Fact]
    public void FileStorageOptions_binds_the_local_filesystem_defaults()
    {
        var options = Resolve<FileStorageOptions>();

        Assert.Equal("Local", options.Provider);
        Assert.Equal("App_Data/uploads", options.LocalRootPath);
        Assert.Equal("/uploads", options.PublicBasePath);
        Assert.Equal(5_000_000, options.MaxUploadBytes);

        // AllowedContentTypes is deliberately NOT in appsettings.json — the configuration binder APPENDS
        // to (rather than replaces) an existing array property, so listing it there would duplicate the
        // code default. The common image types therefore come from the class default (safe when a
        // deployment omits the section); config may still override the scalar members above.
        Assert.Equal(ExpectedImageContentTypes, options.AllowedContentTypes);
    }

    // IOptions<T> is a root singleton, so resolving from the factory's provider both boots the app and
    // returns the bound value — proving BindConfiguration + lazy data-annotation validation succeeded.
    private TOptions Resolve<TOptions>()
        where TOptions : class =>
        _factory.Services.GetRequiredService<IOptions<TOptions>>().Value;
}
