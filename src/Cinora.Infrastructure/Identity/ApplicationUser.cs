using Microsoft.AspNetCore.Identity;

namespace Cinora.Infrastructure.Identity;

/// <summary>
/// Cinora's ASP.NET Identity principal. It carries the authentication surface only — credentials,
/// email confirmation, security stamp, external logins, and lockout — and adds no profile or social
/// state. That state lives on the pure <see cref="Cinora.Domain.Entities.User"/> aggregate, which
/// shares this principal's <see cref="Guid"/> primary key in a 1:1 relationship (ADR 0003).
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
}
