using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Exceptions;
using Cinora.Web.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinora.Web.Controllers;

/// <summary>
/// Diagnostics endpoints (Development and Testing only) that exercise the request pipeline and the global
/// exception handler through the real HTTP stack (used by the integration tests). Every action first checks
/// the hosting environment and returns 404 anywhere other than Development or Testing, so these routes are
/// never reachable in Production — or a public Staging — even if the controller ships in the published
/// assembly. All actions are <see cref="AllowAnonymousAttribute"/> because the global fallback authorization
/// policy is fail-closed and the error output must be reachable anonymously; they are GET so the global
/// anti-forgery filter does not apply.
/// </summary>
[AllowAnonymous]
[Route("diagnostics")]
public sealed class DiagnosticsController(IWebHostEnvironment environment) : Controller
{
    // Diagnostics are exposed only in Development and Testing. Guarding on the positive set (rather than
    // !IsProduction()) means any other environment — notably a public Staging — 404s these routes.
    private bool DiagnosticsUnavailable =>
        !(environment.IsDevelopment() || environment.IsEnvironment("Testing"));

    /// <summary>
    /// Sends a <see cref="PingCommand"/> through the mediator. A valid message returns 200; an empty or
    /// over-length message fails the validator and surfaces as a 400 ValidationProblemDetails via the
    /// global handler (never caught here).
    /// </summary>
    /// <param name="sender">The mediator dispatcher.</param>
    /// <param name="message">The message to echo (required, max 50 chars).</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>200 with the echoed message, or 404 outside Development/Testing.</returns>
    [HttpGet("ping")]
    public async Task<IActionResult> Ping(
        [FromServices] ISender sender,
        [FromQuery] string? message,
        CancellationToken cancellationToken)
    {
        if (DiagnosticsUnavailable)
        {
            return NotFound();
        }

        var result = await sender.Send(new PingCommand(message), cancellationToken);
        return Ok(result);
    }

    /// <summary>Throws an unhandled exception to prove the global handler returns a 500 ProblemDetails.</summary>
    /// <returns>404 outside Development/Testing; otherwise this never returns — it throws.</returns>
    [HttpGet("throw")]
    public IActionResult Throw()
    {
        if (DiagnosticsUnavailable)
        {
            return NotFound();
        }

        throw new InvalidOperationException("Forced diagnostics failure to exercise the 500 ProblemDetails path.");
    }

    /// <summary>Throws a <see cref="DomainException"/> to prove it maps to 400 (backlog item M2).</summary>
    /// <returns>404 outside Development/Testing; otherwise this never returns — it throws.</returns>
    [HttpGet("domain-error")]
    public IActionResult DomainError()
    {
        if (DiagnosticsUnavailable)
        {
            return NotFound();
        }

        throw new DomainException("A domain invariant was violated (diagnostics).");
    }

    /// <summary>Throws a <see cref="NotFoundException"/> to prove it maps to 404.</summary>
    /// <returns>404 outside Development/Testing; otherwise this never returns — it throws.</returns>
    [HttpGet("missing")]
    public IActionResult Missing()
    {
        if (DiagnosticsUnavailable)
        {
            return NotFound();
        }

        throw new NotFoundException("The requested diagnostics resource was not found.");
    }
}
