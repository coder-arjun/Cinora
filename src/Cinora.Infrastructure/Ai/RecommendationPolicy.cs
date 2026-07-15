using Cinora.Application.Common.Interfaces;
using Cinora.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Ai;

/// <summary>
/// The Infrastructure implementation of <see cref="IRecommendationPolicy"/> over
/// <see cref="RecommendationOptions"/>. It exposes the recommendation caps to the Application seams (the taste
/// builder, the candidate source, and the generation handler) without the Application referencing this Options
/// type — the dependency rule (ADR 0001), mirroring the Phase-4 <c>UploadPolicy</c>/<c>IUploadPolicy</c>
/// precedent. Reads <c>IOptions&lt;RecommendationOptions&gt;.Value</c> (bound once at startup — the caps are boot
/// configuration, not hot-reloaded; switch to <c>IOptionsMonitor</c> if live reload is ever wanted).
/// </summary>
internal sealed class RecommendationPolicy : IRecommendationPolicy
{
    private readonly IOptions<RecommendationOptions> _options;

    /// <summary>Initializes the policy over the bound recommendation options.</summary>
    /// <param name="options">The recommendation options supplying the caps.</param>
    public RecommendationPolicy(IOptions<RecommendationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public int MaxTopRated => _options.Value.MaxTopRated;

    /// <inheritdoc />
    public int MaxWatchlistTitles => _options.Value.MaxWatchlistTitles;

    /// <inheritdoc />
    public int MaxFavoriteGenres => _options.Value.MaxFavoriteGenres;

    /// <inheritdoc />
    public int MaxRecommendationSeeds => _options.Value.MaxRecommendationSeeds;

    /// <inheritdoc />
    public int MaxCandidates => _options.Value.MaxCandidates;

    /// <inheritdoc />
    public int MaxResults => _options.Value.MaxResults;

    /// <inheritdoc />
    public int MaxReasonLength => _options.Value.MaxReasonLength;
}
