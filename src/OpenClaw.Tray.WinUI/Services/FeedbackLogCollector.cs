using System.IO.Compression;
using OpenClaw.Connection.NativeGateway;

namespace OpenClawTray.Services;

internal sealed class FeedbackLogCollector(
    INativeGatewayPackageResolver resolver,
    NativeGatewayPackageClient client)
{
    public async Task<NativeGatewayLogBundle> CollectAsync(
        string directory, string? packageFamily, CancellationToken cancellationToken)
    {
        NativeGatewayPackage package = packageFamily is null
            ? await resolver.ResolveAsync(cancellationToken)
            : await resolver.ResolveAsync(packageFamily, cancellationToken);
        string physicalDirectory = resolver.ResolveDataPath(Path.GetFullPath(directory));
        Directory.CreateDirectory(physicalDirectory);
        string path = Path.Combine(physicalDirectory, $"openclaw-feedback-{Guid.NewGuid():N}.zip");
        try
        {
            NativeGatewayLogBundle bundle = await client.CollectLogsAsync(package, path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count == 0)
                throw new InvalidDataException("The diagnostics ZIP is empty.");
            return bundle;
        }
        catch
        {
            // Only this invocation's output can be removed. Never follow a path returned by the CLI.
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Feedback ZIP cleanup failed ({ex.GetType().Name}).");
            }
            throw;
        }
    }
}
