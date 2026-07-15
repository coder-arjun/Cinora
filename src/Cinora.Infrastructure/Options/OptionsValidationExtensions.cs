using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Options;

/// <summary>
/// A self-contained data-annotations validation hook for the Options pattern that adds no NuGet
/// dependency. The framework's <c>OptionsBuilder&lt;T&gt;.ValidateDataAnnotations()</c> lives in the
/// <c>Microsoft.Extensions.Options.DataAnnotations</c> assembly, which the Infrastructure class library
/// does not reference (it is part of the ASP.NET Core shared framework, not pulled transitively by
/// EF Core). Rather than add that package — Milestone 1.5 is deliberately dependency-free — this
/// registers an equivalent <see cref="IValidateOptions{TOptions}"/> built on the base-runtime
/// <see cref="Validator"/>. Behaviour matches the framework: validation runs lazily on first access to
/// <c>IOptions&lt;T&gt;.Value</c>, and (with no <c>ValidateOnStart</c>) never at boot — so Phase 1 starts
/// even while unconsumed secrets such as the TMDB API key are absent (solution-structure.md §6).
/// </summary>
public static class OptionsValidationExtensions
{
    /// <summary>
    /// Registers a data-annotations validator for <typeparamref name="TOptions"/> that evaluates the
    /// options instance lazily on first access. The in-house, package-free equivalent of the framework's
    /// <c>ValidateDataAnnotations()</c>; call <c>BindConfiguration(...)</c> before it.
    /// </summary>
    /// <typeparam name="TOptions">The options type being validated.</typeparam>
    /// <param name="builder">The options builder to attach validation to.</param>
    /// <returns>The same <paramref name="builder"/> instance, for chaining.</returns>
    public static OptionsBuilder<TOptions> ValidateUsingDataAnnotations<TOptions>(
        this OptionsBuilder<TOptions> builder)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IValidateOptions<TOptions>>(
            new DataAnnotationsValidateOptions<TOptions>(builder.Name));

        return builder;
    }
}

/// <summary>
/// Validates an options instance against its <see cref="ValidationAttribute"/> data annotations using the
/// base-runtime <see cref="Validator"/>. Mirrors the framework's internal <c>DataAnnotationValidateOptions</c>
/// (including named-options scoping) without taking a dependency on its package.
/// </summary>
/// <typeparam name="TOptions">The options type being validated.</typeparam>
internal sealed class DataAnnotationsValidateOptions<TOptions> : IValidateOptions<TOptions>
    where TOptions : class
{
    private readonly string? _name;

    /// <summary>Creates a validator scoped to the named options instance (empty string for the default).</summary>
    /// <param name="name">The options name this validator applies to; <see langword="null"/> validates all names.</param>
    public DataAnnotationsValidateOptions(string? name) => _name = name;

    /// <summary>Validates the supplied options instance for the requested name.</summary>
    /// <param name="name">The name of the options instance being validated.</param>
    /// <param name="options">The options instance to validate.</param>
    /// <returns>The validation result — success, skip (name mismatch), or failure with member messages.</returns>
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        // Only validate the instance this validator was registered for (default name matches default).
        if (_name is not null && _name != name)
        {
            return ValidateOptionsResult.Skip;
        }

        ArgumentNullException.ThrowIfNull(options);

        var results = new List<ValidationResult>();
        var context = new ValidationContext(options, serviceProvider: null, items: null);

        if (Validator.TryValidateObject(options, context, results, validateAllProperties: true))
        {
            return ValidateOptionsResult.Success;
        }

        var failures = results.Select(result =>
            $"DataAnnotation validation failed for '{typeof(TOptions).Name}' member(s): "
            + $"'{string.Join(", ", result.MemberNames)}' with the error: '{result.ErrorMessage}'.");

        return ValidateOptionsResult.Fail(failures);
    }
}
