using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Options;

/// <summary>
/// Fail-fast validation for the avatar URL-signing key (ADR 0013 §5): in <b>Production</b> the HMAC key that
/// gates who can mint avatar-serving URLs MUST be supplied via configuration
/// (<c>FileStorage:UrlSigningKey</c>, from user-secrets / environment), so a missing key aborts startup rather
/// than silently minting unverifiable URLs. In Development / Testing the key is auto-generated (an ephemeral
/// random key, wired in <c>AddInfrastructure</c>'s <c>PostConfigure</c>), so this validator only enforces the
/// Production requirement. Runs at boot because <see cref="FileStorageOptions"/> is now
/// <c>ValidateOnStart</c>.
/// </summary>
internal sealed class FileStorageProductionValidateOptions : IValidateOptions<FileStorageOptions>
{
    private readonly IHostEnvironment _environment;

    /// <summary>Initializes the validator with the host environment used to detect Production.</summary>
    /// <param name="environment">The host environment.</param>
    public FileStorageProductionValidateOptions(IHostEnvironment environment) => _environment = environment;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, FileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_environment.IsProduction() && string.IsNullOrWhiteSpace(options.UrlSigningKey))
        {
            return ValidateOptionsResult.Fail(
                $"'{FileStorageOptions.SectionName}:{nameof(FileStorageOptions.UrlSigningKey)}' is required in " +
                "Production (supply it via user-secrets or an environment variable) so avatar-serving URLs can " +
                "be signed and verified.");
        }

        return ValidateOptionsResult.Success;
    }
}
