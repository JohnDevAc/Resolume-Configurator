using System.Text.Json;
using System.Xml.Linq;

namespace ResolumeConfigurator.Services;

internal static class AtomicFile
{
    internal static string BackupName(string path) => path + $".before-configurator-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.bak";
    internal static string ReplacementBackupName(string path) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".configurator-{Guid.NewGuid():N}.original");

    internal static Task WriteXmlAsync(string path, XDocument document, CancellationToken ct, bool backup = false) =>
        WriteAsync(path, stream => document.SaveAsync(stream, SaveOptions.DisableFormatting, ct), ct, backup);

    internal static Task WriteJsonAsync<T>(string path, T value, CancellationToken ct) =>
        WriteAsync(path, stream => JsonSerializer.SerializeAsync(stream, value, cancellationToken: ct), ct);

    internal sealed class FileChangedException(string path, bool replaced = false) : IOException(
        $"The file changed during configuration: {path}. Further commits stopped; inspect the operation record and retained backups.")
    {
        internal bool Replaced { get; } = replaced;
    }

    internal static async Task WriteXmlCheckedAsync(string path, XDocument document, string? expected, string? backup, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".configurator-{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteXmlAsync(temporary, document, ct);
            ct.ThrowIfCancellationRequested();
            if (expected is null)
            {
                if (File.Exists(path)) throw new FileChangedException(path);
                try { File.Move(temporary, path, false); }
                catch (IOException) when (File.Exists(path)) { throw new FileChangedException(path); }
                return;
            }
            if (!File.Exists(path)) throw new FileChangedException(path);
            // Prevent in-place writes while allowing atomic replacement. A concurrent
            // rename is detected by comparing the actual replaced file in the backup.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                if (reader.ReadToEnd() != expected) throw new FileChangedException(path);
                ct.ThrowIfCancellationRequested();
                File.Replace(temporary, path, backup ?? throw new ArgumentNullException(nameof(backup)));
            }
            if (File.ReadAllText(backup!) != expected) throw new FileChangedException(path, replaced: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

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
