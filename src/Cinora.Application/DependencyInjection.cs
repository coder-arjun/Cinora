using System.Reflection;
using Cinora.Application.Common.Behaviors;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Recommendations;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Application;

/// <summary>Composition-root wiring for the Application layer (the hand-rolled mediator pipeline).</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers Cinora's hand-rolled mediator and its cross-cutting pipeline: <see cref="ISender"/>, every
    /// <see cref="IRequestHandler{TRequest, TResponse}"/> discovered in this assembly, all FluentValidation
    /// validators discovered in this assembly, and the three pipeline behaviors in execution order —
    /// <see cref="LoggingBehavior{TRequest, TResponse}"/> (outermost) →
    /// <see cref="ValidationBehavior{TRequest, TResponse}"/> →
    /// <see cref="PerformanceBehavior{TRequest, TResponse}"/> (closest to the handler). Phase 1 ships with
    /// no requests yet; the pipeline is scaffolded so real features slot in with zero plumbing.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var applicationAssembly = typeof(DependencyInjection).Assembly;

        // The dispatcher. Scoped so the IServiceProvider it captures is the request scope, letting it
        // resolve scoped handlers (which typically depend on the scoped IAppDbContext).
        services.AddScoped<ISender, Sender>();

        // Discover and register every IRequestHandler<,> implementation in the Application assembly.
        RegisterRequestHandlers(services, applicationAssembly);

        // Discover and register every FluentValidation IValidator<> in the Application assembly. Phase 1
        // has none; ValidationBehavior no-ops for requests without a validator.
        services.AddValidatorsFromAssembly(applicationAssembly, includeInternalTypes: true);

        // Behaviors run in registration order (GetServices preserves it): Logging wraps Validation wraps
        // Performance wraps the handler. Open generics so they apply to every request type.
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(PerformanceBehavior<,>));

        // Application-internal recommendation seams (Milestones 5.1/5.2, ADR 0017/0018). Unlike IRequestHandler<,>/
        // validators these are not auto-discovered by the scans above, so they are registered explicitly. All
        // depend on scoped services (IAppDbContext / ITmdbClient), so all are scoped; the generate command and the
        // serve query handlers that consume them are auto-discovered. The served-recommendation cache is an
        // Infrastructure adapter (registered in AddInfrastructure) — it is the only recommendation seam whose
        // implementation lives outside Application.
        services.AddScoped<IUserTasteProfileBuilder, UserTasteProfileBuilder>();
        services.AddScoped<IRecommendationCandidateSource, RecommendationCandidateSource>();
        services.AddScoped<IHeuristicRecommender, HeuristicRecommender>();

        return services;
    }

    // Registers each concrete IRequestHandler<TRequest,TResponse> against its closed interface. Handlers
    // are transient — cheap to construct and safe to resolve scoped dependencies within a request scope.
    // Every request must have exactly one handler: a second implementation for the same closed handler
    // interface is a loud startup failure (InvalidOperationException naming the request), because the
    // dispatcher resolves a single handler and a silent last-registration-wins would hide the mistake.
    private static void RegisterRequestHandlers(IServiceCollection services, Assembly assembly)
    {
        // Tracks the concrete handler already registered for each closed IRequestHandler<,> interface, so a
        // duplicate is detected at scan time rather than surfacing as ambiguous resolution at send time.
        var registeredHandlers = new Dictionary<Type, Type>();

        foreach (var implementationType in assembly.GetTypes())
        {
            if (implementationType is not { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            {
                continue;
            }

            foreach (var handlerInterface in implementationType.GetInterfaces())
            {
                if (!handlerInterface.IsGenericType
                    || handlerInterface.GetGenericTypeDefinition() != typeof(IRequestHandler<,>))
                {
                    continue;
                }

                if (registeredHandlers.TryGetValue(handlerInterface, out var existingHandler))
                {
                    var requestType = handlerInterface.GetGenericArguments()[0];
                    throw new InvalidOperationException(
                        $"Multiple IRequestHandler implementations were found for request '{requestType.FullName}': " +
                        $"'{existingHandler.FullName}' and '{implementationType.FullName}'. Exactly one handler " +
                        "must be registered per request type.");
                }

                registeredHandlers.Add(handlerInterface, implementationType);
                services.AddTransient(handlerInterface, implementationType);
            }
        }
    }
}
