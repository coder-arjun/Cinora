namespace Cinora.Application.Features.MovieLikes;

/// <summary>The love state of a title for the current viewer: whether they have loved it, and the total count.</summary>
/// <param name="Liked">Whether the current user has loved this title.</param>
/// <param name="Count">The total number of users who have loved this title.</param>
public sealed record MovieLikeVm(bool Liked, int Count);
