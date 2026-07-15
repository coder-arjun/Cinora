using Cinora.Application.Common.Messaging;
using FluentValidation;

namespace Cinora.Web.Diagnostics;

/// <summary>
/// A diagnostics-only request used to exercise the hand-rolled mediator pipeline end-to-end through the
/// real HTTP stack (validation behavior → handler). It carries no business meaning and is registered only
/// outside Production. See <c>DiagnosticsController</c>.
/// </summary>
/// <param name="Message">A short echo message; validated as required and length-limited.</param>
public sealed record PingCommand(string? Message) : IRequest<string>;

/// <summary>Validates <see cref="PingCommand"/>: the message is required and at most 50 characters.</summary>
public sealed class PingCommandValidator : AbstractValidator<PingCommand>
{
    /// <summary>Configures the rules for <see cref="PingCommand"/>.</summary>
    public PingCommandValidator()
    {
        RuleFor(command => command.Message)
            .NotEmpty().WithMessage("Message is required.")
            .MaximumLength(50).WithMessage("Message must be 50 characters or fewer.");
    }
}

/// <summary>Handles <see cref="PingCommand"/> by echoing the message — proving the pipeline reached the handler.</summary>
public sealed class PingCommandHandler : IRequestHandler<PingCommand, string>
{
    /// <inheritdoc />
    public Task<string> Handle(PingCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult($"pong: {request.Message}");
    }
}
