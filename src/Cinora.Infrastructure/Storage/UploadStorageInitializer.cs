using Cinora.Infrastructure.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Storage;

/// <summary>
/// Ensures the local upload root (<see cref="FileStorageOptions.LocalRootPath"/>, outside <c>wwwroot</c>)
/// exists at application startup, so the first avatar upload does not fail on a missing folder (ADR 0013 §2.2).
/// A hosted service rather than lazy creation in the storage adapter keeps the pure, per-view
/// <see cref="Cinora.Application.Common.Interfaces.IFileStorage.GetUrl"/> path free of any filesystem I/O.
/// </summary>
internal sealed class UploadStorageInitializer : IHostedService
{
    private readonly IHostEnvironment _environment;
    private readonly IOptions<FileStorageOptions> _options;

    /// <summary>Initializes the initializer with the host environment and bound options.</summary>
    /// <param name="environment">The host environment supplying the content root.</param>
    /// <param name="options">The bound file-storage options supplying the relative root path.</param>
    public UploadStorageInitializer(IHostEnvironment environment, IOptions<FileStorageOptions> options)
    {
        _environment = environment;
        _options = options;
    }

    /// <summary>Creates the upload-root directory if it does not already exist.</summary>
    /// <param name="cancellationToken">Unused — directory creation is synchronous and immediate.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var root = AvatarPaths.ResolveRoot(_environment, _options.Value);
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    /// <summary>No-op on shutdown.</summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
