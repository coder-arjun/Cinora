using Cinora.Application;
using Cinora.Application.Common.Behaviors;
using Cinora.Application.Common.Messaging;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using ValidationException = Cinora.Application.Common.Exceptions.ValidationException;

namespace Cinora.Application.Tests.Common.Messaging;

/// <summary>
/// Exercises the hand-rolled mediator and its pipeline end-to-end using a real
/// <see cref="ServiceProvider"/> built from <see cref="DependencyInjection.AddApplication"/> plus a
/// test-only request, handler, validator, and probe behavior. No third-party mediator is involved.
/// </summary>
public sealed class MediatorPipelineTests
{
    // The expected outermost-first execution trace for the recording-behavior order test. Hoisted to a
    // static field (rather than an inline array literal) to satisfy CA1861.
    private static readonly string[] ExpectedBehaviorExecutionOrder =
        ["Logging", "Validation", "Performance", "Handler"];

    // Builds a provider with the real Application pipeline, then registers the test-only request wiring
    // (the assembly scan in AddApplication only sees the Application assembly, not this test assembly).
    private static ServiceProvider BuildProvider(HandlerProbe handlerProbe, BehaviorProbe behaviorProbe)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();

        services.AddSingleton(handlerProbe);
        services.AddSingleton(behaviorProbe);
        services.AddTransient<IRequestHandler<SampleRequest, string>, SampleHandler>();
        services.AddScoped<IValidator<SampleRequest>, SampleRequestValidator>();

        // An extra behavior appended after AddApplication proves the chain composes around the handler.
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ProbeBehavior<,>));

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Send_with_a_valid_request_returns_the_handler_result_through_the_pipeline()
    {
        var handlerProbe = new HandlerProbe();
        var behaviorProbe = new BehaviorProbe();
        await using var provider = BuildProvider(handlerProbe, behaviorProbe);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var result = await sender.Send(new SampleRequest("Cinora"), CancellationToken.None);

        Assert.Equal("handled:Cinora", result);
        Assert.True(handlerProbe.Handled);
    }

    [Fact]
    public async Task Send_with_an_invalid_request_throws_ValidationException_before_the_handler_runs()
    {
        var handlerProbe = new HandlerProbe();
        var behaviorProbe = new BehaviorProbe();
        await using var provider = BuildProvider(handlerProbe, behaviorProbe);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => sender.Send(new SampleRequest(string.Empty), CancellationToken.None));

        // The ValidationBehavior short-circuited the pipeline: the handler never executed.
        Assert.False(handlerProbe.Handled);
        Assert.True(exception.Errors.ContainsKey(nameof(SampleRequest.Name)));
        Assert.Contains("Name is required.", exception.Errors[nameof(SampleRequest.Name)]);
    }

    [Fact]
    public async Task Send_composes_pipeline_behaviors_around_the_handler()
    {
        var handlerProbe = new HandlerProbe();
        var behaviorProbe = new BehaviorProbe();
        await using var provider = BuildProvider(handlerProbe, behaviorProbe);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send(new SampleRequest("Cinora"), CancellationToken.None);

        // The probe behavior ran (behaviors compose), and it recorded that it ran BEFORE the handler.
        Assert.True(behaviorProbe.Executed);
        Assert.True(behaviorProbe.RanBeforeHandler);
    }

    [Fact]
    public async Task Send_executes_behaviors_in_registration_order_outermost_first_then_the_handler()
    {
        // Three recording behaviors registered in order [Logging, Validation, Performance] (mirroring the
        // real pipeline) each append their name before calling next; the handler appends last. This locks in
        // that the dispatcher runs the first-registered behavior outermost and the handler innermost.
        var executionOrder = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ISender, Sender>();
        services.AddTransient<IRequestHandler<OrderRequest, string>>(
            _ => new OrderRecordingHandler(executionOrder));
        services.AddTransient<IPipelineBehavior<OrderRequest, string>>(
            _ => new RecordingBehavior<OrderRequest, string>("Logging", executionOrder));
        services.AddTransient<IPipelineBehavior<OrderRequest, string>>(
            _ => new RecordingBehavior<OrderRequest, string>("Validation", executionOrder));
        services.AddTransient<IPipelineBehavior<OrderRequest, string>>(
            _ => new RecordingBehavior<OrderRequest, string>("Performance", executionOrder));

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send(new OrderRequest("Cinora"), CancellationToken.None);

        Assert.Equal(ExpectedBehaviorExecutionOrder, executionOrder);
    }

    [Fact]
    public void AddApplication_registers_behaviors_in_logging_then_validation_then_performance_order()
    {
        // Ties the generic ordering guarantee above to the concrete named pipeline: AddApplication must
        // register exactly Logging → Validation → Performance, in that order, so the composed execution order
        // is Logging → Validation → Performance → handler.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();

        using var provider = services.BuildServiceProvider();

        var behaviorTypes = provider
            .GetServices<IPipelineBehavior<SampleRequest, string>>()
            .Select(behavior => behavior.GetType().GetGenericTypeDefinition())
            .ToArray();

        Assert.Equal(
            new[]
            {
                typeof(LoggingBehavior<,>),
                typeof(ValidationBehavior<,>),
                typeof(PerformanceBehavior<,>),
            },
            behaviorTypes);
    }

    [Fact]
    public async Task Send_with_a_request_that_has_no_validator_passes_straight_through()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddTransient<IRequestHandler<NoValidatorRequest, int>, NoValidatorHandler>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        // No IValidator<NoValidatorRequest> is registered: ValidationBehavior must no-op, not throw.
        var result = await sender.Send(new NoValidatorRequest(21), CancellationToken.None);

        Assert.Equal(42, result);
    }

    // ---- test-only sample use case + probes ----

    private sealed record SampleRequest(string Name) : IRequest<string>;

    private sealed class SampleRequestValidator : AbstractValidator<SampleRequest>
    {
        public SampleRequestValidator() =>
            RuleFor(request => request.Name).NotEmpty().WithMessage("Name is required.");
    }

    private sealed class SampleHandler(HandlerProbe probe) : IRequestHandler<SampleRequest, string>
    {
        public Task<string> Handle(SampleRequest request, CancellationToken cancellationToken)
        {
            probe.Handled = true;
            return Task.FromResult($"handled:{request.Name}");
        }
    }

    private sealed record OrderRequest(string Name) : IRequest<string>;

    // Appends "Handler" to the shared execution log so a behavior-order test can prove the handler ran last.
    private sealed class OrderRecordingHandler(List<string> executionOrder)
        : IRequestHandler<OrderRequest, string>
    {
        public Task<string> Handle(OrderRequest request, CancellationToken cancellationToken)
        {
            executionOrder.Add("Handler");
            return Task.FromResult($"handled:{request.Name}");
        }
    }

    // Appends its label to the shared execution log on entry (before calling next), so the recorded sequence
    // reflects the outermost-first order in which the dispatcher composes registered behaviors.
    private sealed class RecordingBehavior<TRequest, TResponse>(string label, List<string> executionOrder)
        : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public async Task<TResponse> Handle(
            TRequest request,
            RequestHandlerDelegate<TResponse> next,
            CancellationToken cancellationToken)
        {
            executionOrder.Add(label);
            return await next(cancellationToken);
        }
    }

    private sealed record NoValidatorRequest(int Value) : IRequest<int>;

    private sealed class NoValidatorHandler : IRequestHandler<NoValidatorRequest, int>
    {
        public Task<int> Handle(NoValidatorRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Value * 2);
    }

    private sealed class HandlerProbe
    {
        public bool Handled { get; set; }
    }

    private sealed class BehaviorProbe
    {
        public bool Executed { get; set; }

        public bool RanBeforeHandler { get; set; }
    }

    private sealed class ProbeBehavior<TRequest, TResponse>(BehaviorProbe probe, HandlerProbe handlerProbe)
        : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public async Task<TResponse> Handle(
            TRequest request,
            RequestHandlerDelegate<TResponse> next,
            CancellationToken cancellationToken)
        {
            probe.Executed = true;
            probe.RanBeforeHandler = !handlerProbe.Handled; // handler must not have run yet at this point
            return await next(cancellationToken);
        }
    }
}
