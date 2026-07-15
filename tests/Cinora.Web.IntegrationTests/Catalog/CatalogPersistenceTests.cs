using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Catalog;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace Cinora.Web.IntegrationTests.Catalog;

/// <summary>
/// Verifies Milestone 2.2 catalog persistence end-to-end through the real Application pipeline (hand-rolled
/// <see cref="ISender"/> + validation behavior) against the migrated <c>CinoraTest</c> LocalDB. The real
/// HTTP-backed <see cref="ITmdbClient"/> is replaced per test with a <see cref="FakeTmdbClient"/> via a
/// <c>WithWebHostBuilder</c>-derived host so the shared factory is never mutated; the static Serilog logger
/// that building a host installs is saved and restored, matching the guard the sibling suites rely on in
/// this non-parallel collection. Each test uses TMDB ids outside the seeded set so the shared database stays
/// isolated.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class CatalogPersistenceTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public CatalogPersistenceTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SyncGenres_inserts_the_canned_genres_and_is_idempotent_on_a_second_run()
    {
        // Genre ids deliberately outside the seeded/real TMDB set so the assertions stay isolated on the DB.
        var fake = new FakeTmdbClient
        {
            MovieGenres =
            [
                new TmdbGenre { TmdbGenreId = 900_001, Name = "Test Movie Genre A" },
                new TmdbGenre { TmdbGenreId = 900_002, Name = "Test Movie Genre B" },
            ],
            SeriesGenres =
            [
                new TmdbGenre { TmdbGenreId = 900_003, Name = "Test Series Genre C" },
            ],
        };
        int[] cannedIds = [900_001, 900_002, 900_003];

        using var factory = CreateFactoryWith(fake);

        var first = await SendAsync(factory, new SyncGenresCommand());
        var presentAfterFirst = await GenreCountAsync(factory, cannedIds);

        var second = await SendAsync(factory, new SyncGenresCommand());
        var presentAfterSecond = await GenreCountAsync(factory, cannedIds);

        Assert.Equal(3, first.Inserted);
        Assert.Equal(3, presentAfterFirst); // all three canned genres now exist
        Assert.Equal(0, second.Inserted); // nothing new on the second run
        Assert.Equal(3, presentAfterSecond); // still three — the upsert produced no duplicates
    }

    [Fact]
    public async Task EnsureTitleCached_creates_the_movie_with_mapped_fields_and_links_and_is_idempotent()
    {
        const int tmdbId = 900_500;
        var details = new TmdbTitleDetails
        {
            TmdbId = tmdbId,
            MediaType = MediaType.Movie,
            Title = "Test Title 900500",
            Overview = "A canned overview.",
            PosterPath = "/poster900500.jpg",
            BackdropPath = "/backdrop900500.jpg",
            ReleaseDate = new DateOnly(2021, 3, 14),
            VoteAverage = 7.5,
            VoteCount = 100,
            Runtime = 123,
            Genres =
            [
                new TmdbGenre { TmdbGenreId = 28, Name = "Action" },            // seeded → resolves to existing
                new TmdbGenre { TmdbGenreId = 900_101, Name = "Test Genre X" }, // missing → upserted so linking works
            ],
        };
        var fake = new FakeTmdbClient();
        fake.Details[(MediaType.Movie, tmdbId)] = details;

        using var factory = CreateFactoryWith(fake);

        var created = await SendAsync(factory, new EnsureTitleCachedCommand(tmdbId, MediaType.Movie));

        Assert.Equal(EnsureTitleOutcome.Created, created.Outcome);
        Assert.NotNull(created.MovieId);

        var (movie, linkedGenreTmdbIds) = await LoadMovieAsync(factory, tmdbId, MediaType.Movie);

        Assert.NotNull(movie);
        Assert.Equal(created.MovieId, movie!.Id);
        Assert.Equal("Test Title 900500", movie.Title);
        Assert.Equal("A canned overview.", movie.Overview);
        Assert.Equal("/poster900500.jpg", movie.PosterPath);
        Assert.Equal("/backdrop900500.jpg", movie.BackdropPath);
        Assert.Equal(new DateTime(2021, 3, 14), movie.ReleaseDate); // DateOnly → DateTime (midnight)

        int[] expectedGenreTmdbIds = [28, 900_101];
        Assert.Equal(expectedGenreTmdbIds, linkedGenreTmdbIds); // both genres linked, the missing one upserted

        // Idempotent: a second dispatch creates nothing and returns the same internal id.
        var again = await SendAsync(factory, new EnsureTitleCachedCommand(tmdbId, MediaType.Movie));

        Assert.Equal(EnsureTitleOutcome.AlreadyCached, again.Outcome);
        Assert.Equal(created.MovieId, again.MovieId);
        Assert.Equal(1, await MovieCountAsync(factory, tmdbId, MediaType.Movie)); // exactly one Movie row
    }

    [Fact]
    public async Task EnsureTitleCached_for_a_details_404_creates_no_movie_and_returns_not_found()
    {
        const int tmdbId = 900_700;
        var fake = new FakeTmdbClient(); // no Details entry → GetDetailsAsync returns null (models a 404)

        using var factory = CreateFactoryWith(fake);

        var result = await SendAsync(factory, new EnsureTitleCachedCommand(tmdbId, MediaType.Movie));

        Assert.Equal(EnsureTitleOutcome.NotFound, result.Outcome);
        Assert.Null(result.MovieId);
        Assert.Equal(0, await MovieCountAsync(factory, tmdbId, MediaType.Movie)); // no row created
    }

    [Fact]
    public async Task EnsureTitleCached_with_a_non_positive_tmdb_id_fails_validation_before_the_handler()
    {
        var fake = new FakeTmdbClient();
        using var factory = CreateFactoryWith(fake);

        // TmdbId 0 trips EnsureTitleCachedCommandValidator inside the ValidationBehavior, so the handler
        // never runs and no TMDB call / write occurs.
        await Assert.ThrowsAsync<ValidationException>(
            () => SendAsync(factory, new EnsureTitleCachedCommand(0, MediaType.Movie)));
    }

    // Builds a fake-backed derived host (leaving the shared factory untouched) and restores the process-global
    // Serilog logger that building the host installs, so the sibling non-parallel suites keep the base logger.
    private WebApplicationFactory<Program> CreateFactoryWith(ITmdbClient fake)
    {
        var originalLogger = Log.Logger;
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITmdbClient>();
                services.AddScoped(_ => fake);
            }));

        try
        {
            // Force the host to build now so its static-logger side effect is captured and undone below.
            _ = factory.Services;
        }
        finally
        {
            Log.Logger = originalLogger;
        }

        return factory;
    }

    private static async Task<TResponse> SendAsync<TResponse>(
        WebApplicationFactory<Program> factory,
        IRequest<TResponse> request)
    {
        using var scope = factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        return await sender.Send(request);
    }

    private static async Task<int> GenreCountAsync(WebApplicationFactory<Program> factory, int[] tmdbGenreIds)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Genres.AsNoTracking().CountAsync(genre => tmdbGenreIds.Contains(genre.TmdbGenreId));
    }

    private static async Task<int> MovieCountAsync(
        WebApplicationFactory<Program> factory,
        int tmdbId,
        MediaType mediaType)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Movies
            .AsNoTracking()
            .CountAsync(movie => movie.TmdbId == tmdbId && movie.MediaType == mediaType);
    }

    private static async Task<(Movie? Movie, int[] LinkedGenreTmdbIds)> LoadMovieAsync(
        WebApplicationFactory<Program> factory,
        int tmdbId,
        MediaType mediaType)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var movie = await db.Movies
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.TmdbId == tmdbId && m.MediaType == mediaType);

        if (movie is null)
        {
            return (null, []);
        }

        var linkedGenreTmdbIds = await db.MovieGenres
            .AsNoTracking()
            .Where(link => link.MovieId == movie.Id)
            .Join(db.Genres, link => link.GenreId, genre => genre.Id, (link, genre) => genre.TmdbGenreId)
            .OrderBy(id => id)
            .ToArrayAsync();

        return (movie, linkedGenreTmdbIds);
    }
}
