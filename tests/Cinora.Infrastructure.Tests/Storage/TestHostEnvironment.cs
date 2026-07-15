using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Cinora.Infrastructure.Tests.Storage;

/// <summary>
/// A throwaway <see cref="IHostEnvironment"/> rooted at a unique temp directory, so the local storage adapter
/// writes real files under an isolated content root and the test can assert their location. Deletes the whole
/// tree on <see cref="Dispose"/>.
/// </summary>
internal sealed class TestHostEnvironment : IHostEnvironment, IDisposable
{
    public TestHostEnvironment(string environmentName = "Testing")
    {
        EnvironmentName = environmentName;
        ApplicationName = "Cinora.Infrastructure.Tests";
        ContentRootPath = Path.Combine(Path.GetTempPath(), "cinora-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ContentRootPath);
        ContentRootFileProvider = new NullFileProvider();
    }

    public string ApplicationName { get; set; }

    public string EnvironmentName { get; set; }

    public string ContentRootPath { get; set; }

    public IFileProvider ContentRootFileProvider { get; set; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(ContentRootPath))
            {
                Directory.Delete(ContentRootPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp tree; a locked file must not fail the test run.
        }
    }
}
