using System.Xml.Linq;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

internal sealed class ConfigurationFiles
{
    public string OperationId { get; } = Guid.NewGuid().ToString("N");
    public string OperationDirectory { get; }
    public string CompositionFile { get; }
    public string PresetFile { get; }
    public string? CompositionBackup { get; set; }
    public string? PreviousComposition { get; set; }
    private readonly ArenaUserPaths _paths;
    private readonly List<FileChange> _changes = [];
    private readonly Dictionary<string, string?> _originals = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _committed = [];
    private sealed record FileChange(string Destination, string? Backup, string Status);

    private ConfigurationFiles(ConfigurationPlan plan, ArenaUserPaths paths)
    {
        _paths = paths;
        OperationDirectory = Path.Combine(Path.GetFullPath(plan.CompositionDirectory), "Configurator Backups", OperationId);
        CompositionFile = ArenaPaths.OutputPath(plan.CompositionDirectory, plan.CompositionName, ".avc");
        PresetFile = ArenaPaths.OutputPath(plan.PresetDirectory, plan.PresetName, ".xml");
    }

    internal static async Task<ConfigurationFiles> PrepareAsync(ConfigurationPlan plan, ArenaUserPaths paths, ArenaProduct product, CancellationToken ct)
    {
        var files = new ConfigurationFiles(plan, paths);
        if (!Directory.Exists(paths.Root) || !Directory.Exists(Path.Combine(paths.Root, "Preferences")))
            throw new InvalidOperationException($"Arena's user-data folder was not found at {paths.Root}. Start Arena once, or set RESOLUME_ARENA_DATA_DIR to its actual folder.");
        foreach (var target in new[] { files.CompositionFile, files.PresetFile, paths.AdvancedOutputPreference, paths.SimpleOutputPreference })
            ProbeDestination(target);
        ValidateXml(files.CompositionFile, "Composition");
        ValidateXml(files.PresetFile, "XmlState", "ScreenSetup");
        ValidateXml(paths.AdvancedOutputPreference, "ScreenSetup");
        ValidateXml(paths.SimpleOutputPreference, "SimpleSetup");
        Directory.CreateDirectory(files.OperationDirectory);
        foreach (var target in new[] { files.PresetFile, paths.AdvancedOutputPreference, paths.SimpleOutputPreference })
            files._originals[target] = File.Exists(target) ? await File.ReadAllTextAsync(target, ct) : null;
        await files.StageAsync(plan, new AdvancedOutputPresetGenerator().Generate(plan, product), ct);
        await files.RecordAsync("prepared", null, ct);
        return files;
    }

    internal static void ProbeDestination(string path)
    {
        path = Path.GetFullPath(path);
        if (path.Length > 240) throw new InvalidOperationException($"Arena output paths must fit within 240 characters. Select a shorter data directory: {path}");
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (Directory.Exists(path)) throw new IOException($"A directory occupies the required file path: {path}");
        if (File.Exists(path))
        {
            using var existing = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        var probe = Path.Combine(directory, $".configurator-probe-{Guid.NewGuid():N}.tmp");
        var moved = probe + ".moved";
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Move(probe, moved);
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
            if (File.Exists(moved)) File.Delete(moved);
        }
    }

    private static void ValidateXml(string path, params string[] roots)
    {
        if (!File.Exists(path)) return;
        try
        {
            if (!roots.Contains(XDocument.Load(path).Root?.Name.LocalName))
                throw new InvalidDataException($"Unexpected XML root in {path}.");
        }
        catch (System.Xml.XmlException ex) { throw new InvalidDataException($"Repair or restore the malformed XML before configuring Arena: {path}", ex); }
    }

    public async Task StageAsync(ConfigurationPlan plan, XDocument preset, CancellationToken ct)
    {
        var screens = new[] { "Show", "LED Wall" }.Concat(plan.Decoders.Select(d => d.OutputName)).ToArray();
        await AtomicFile.WriteXmlAsync(Path.Combine(OperationDirectory, "preset.xml"), preset, ct);
        await AtomicFile.WriteXmlAsync(Path.Combine(OperationDirectory, "advanced.xml"), AdvancedOutputActivator.CreateActiveDocument(preset, screens), ct);
        await AtomicFile.WriteXmlAsync(Path.Combine(OperationDirectory, "simple.xml"),
            SimpleOutputConfigurationService.CreateDocument(_paths.SimpleOutputPreference, plan.EnableNdiCompositionSharing,
                plan.CompositionWidth, plan.CompositionHeight), ct);
    }

    public async Task CommitAsync(Func<CancellationToken, Task> validate, CancellationToken ct)
    {
        var targets = new[] { (PresetFile, "preset.xml"), (_paths.AdvancedOutputPreference, "advanced.xml"), (_paths.SimpleOutputPreference, "simple.xml") };
        foreach (var (target, _) in targets)
        {
            ProbeDestination(target);
            var current = File.Exists(target) ? await File.ReadAllTextAsync(target, ct) : null;
            if (current != _originals[target]) throw new IOException($"Arena preferences/preset changed during configuration: {target}. Review it before retrying.");
        }
        var writingOutput = false;
        try
        {
            foreach (var (target, staged) in targets)
            {
                await validate(ct);
                writingOutput = true;
                // Windows atomic replacement requires the backup on the target's
                // volume; the composition/recovery directory may be on another drive.
                var backup = _originals[target] is null ? null : AtomicFile.ReplacementBackupName(target);
                _changes.Add(new(target, backup, "pending"));
                await RecordAsync("committing output files", null, ct);
                await AtomicFile.WriteXmlCheckedAsync(target, XDocument.Load(Path.Combine(OperationDirectory, staged)), _originals[target], backup, ct);
                _committed.Add(target);
                _changes[^1] = new(target, backup, "committed");
                await RecordAsync("committing output files", null, ct);
                writingOutput = false;
            }
        }
        catch (AtomicFile.FileChangedException ex)
        {
            _changes[^1] = _changes[^1] with { Status = ex.Replaced ? "conflict; replaced file retained in backup" : "changed before replacement" };
            await RecordAsync("output conflict; review backups", ex.Message, CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (writingOutput && (ex is IOException or UnauthorizedAccessException))
        {
            // Recover file-write failures. A job/readiness guard failure instead
            // stops all further writes and leaves the manifest for explicit recovery.
            foreach (var target in _committed.AsEnumerable().Reverse())
            {
                var index = _changes.FindIndex(change => change.Destination == target);
                var change = _changes[index];
                try
                {
                    if (change.Backup is null) File.Delete(target);
                    else await AtomicFile.WriteXmlAsync(target, XDocument.Load(change.Backup), CancellationToken.None);
                    _changes[index] = change with { Status = "rolled back" };
                }
                catch (Exception rollbackError) { _changes[index] = change with { Status = "rollback failed: " + rollbackError.Message }; }
            }
            await RecordAsync("output commit failed", ex.Message, CancellationToken.None);
            throw;
        }
    }

    public Task RecordAsync(string status, string? error, CancellationToken ct) =>
        AtomicFile.WriteJsonAsync(Path.Combine(OperationDirectory, "operation.json"),
            new { OperationId, Status = status, Error = error, CompositionFile, CompositionBackup, PreviousComposition, Files = _changes, UpdatedUtc = DateTime.UtcNow }, ct);
}
