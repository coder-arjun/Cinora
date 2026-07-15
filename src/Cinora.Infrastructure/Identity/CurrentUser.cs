using System.Security.Claims;
using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Cinora.Infrastructure.Identity;

/// <summary>
/// The <see cref="ICurrentUser"/> adapter (ADR 0009): resolves the acting user's id from the current
/// request's <see cref="ClaimsPrincipal"/> — the <see cref="ClaimTypes.NameIdentifier"/> claim ASP.NET
/// Identity stamps with <see cref="ApplicationUser"/>'s <see cref="Guid"/> key (the same claim the SignalR
/// notification hub reads in Milestone 3.5). Scoped, over <see cref="IHttpContextAccessor"/>; a background
/// context (no HTTP request, e.g. a future Hangfire job) reads as anonymous.
/// </summary>
/// <param name="httpContextAccessor">Accessor for the current request's <see cref="HttpContext"/>.</param>
public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    /// <inheritdoc />
    public Guid? UserId =>
        Guid.TryParse(
            httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier),
            out var userId)
            ? userId
            : null;

    /// <inheritdoc />
    public bool IsAuthenticated =>
        httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated ?? false;

    /// <inheritdoc />
    public Guid GetRequiredUserId() =>
        UserId ?? throw new ForbiddenAccessException("No authenticated user for the current request.");
}
