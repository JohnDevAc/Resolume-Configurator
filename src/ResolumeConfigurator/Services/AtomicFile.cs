using System.Text.Json;
using System.Xml.Linq;

namespace ResolumeConfigurator.Services;

internal static class AtomicFile
{
    internal static string BackupName(string path) => path + $".before-configurator-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.bak";

    internal static Task WriteXmlAsync(string path, XDocument document, CancellationToken ct, bool backup = false) =>
        WriteAsync(path, stream => document.SaveAsync(stream, SaveOptions.DisableFormatting, ct), ct, backup);

    internal static Task WriteJsonAsync<T>(string path, T value, CancellationToken ct) =>
        WriteAsync(path, stream => JsonSerializer.SerializeAsync(stream, value, cancellationToken: ct), ct);

    private static async Task WriteAsync(string path, Func<Stream, Task> write, CancellationToken ct, bool backup = false)
    {
        ct.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".configurator-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await write(stream).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, backup ? BackupName(path) : null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
