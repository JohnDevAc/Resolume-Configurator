using System.Net.Http;
using System.Xml.Linq;

namespace ResolumeConfigurator.Services;

public sealed class ArenaCompositionSynchronizationService
{
    public string? BackupFile { get; private set; }
    public async Task<ArenaCompositionState> EnsureCurrentAsync(ResolumeApiClient api, string compositionDirectory,
        IProgress<string>? progress, CancellationToken ct)
    {
        var state = await api.GetCompositionStateAsync(ct);
        var backupDirectory = Path.Combine(compositionDirectory, "Configurator Backups", $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupDirectory);
        var backup = ArenaPaths.OutputPath(backupDirectory, string.IsNullOrWhiteSpace(state.Name) ? "Untitled" : state.Name, ".avc");
        await api.SaveCompositionAsync(backup, ct);
        BackupFile = backup;
        var saved = XDocument.Load(backup);
        progress?.Report($"Saved the open composition to {backup}.");

        // Arena 7.27 can keep serializing the previous graph after New/Open.
        // Its save endpoint writes the real graph, while stale clip operations
        // may return success or 404. Compare identities before editing anything.
        state = await api.GetCompositionStateAsync(ct);
        if (MatchesSavedStructure(saved, state)) return state;

        progress?.Report("Arena's API contains stale layers or clips. Restarting with the saved open composition.");
        await new ArenaRestartService().RestartArenaAsync(backup, ct,
            api.BeforeMutation ?? throw new InvalidOperationException("A current job and Agent guard is required before restarting Arena."));
        using var freshApi = new ResolumeApiClient(timeout: TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                state = await freshApi.GetCompositionStateAsync(ct);
                if (MatchesSavedStructure(saved, state))
                {
                    progress?.Report("Arena's live API now matches the saved composition.");
                    return state;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException) { }
            await Task.Delay(500, ct);
        }
        throw new InvalidOperationException("Arena's API did not synchronize with its saved composition after restart.");
    }

    internal static bool MatchesSavedStructure(XDocument saved, ArenaCompositionState state)
    {
        var root = saved.Root;
        if (root?.Name.LocalName != "Composition") return false;
        var layers = root.Elements("Layer").ToArray();
        var groups = root.Elements("Group").ToArray();
        if ((int?)root.Attribute("numColumns") != state.ColumnIds.Count
            || !layers.Select(layer => (long?)layer.Attribute("uniqueId")).SequenceEqual(state.Layers.Select(layer => (long?)layer.Id))
            || !groups.Select(group => (long?)group.Attribute("uniqueId")).SequenceEqual(state.Groups.Select(group => (long?)group.Id)))
            return false;
        for (var index = 0; index < groups.Length; index++)
            if (!layers.Where(layer => (int?)layer.Attribute("layerGroup") == index)
                .Select(layer => (long?)layer.Attribute("uniqueId"))
                .SequenceEqual(state.Groups[index].Layers.Select(layer => (long?)layer.Id))) return false;
        var currentDeck = root.Elements("Deck").ElementAtOrDefault((int?)root.Attribute("currentDeckIndex") ?? 0);
        if (currentDeck is null) return false;
        var savedClips = currentDeck.Elements("Clip")
            .OrderBy(clip => (int?)clip.Attribute("layerIndex"))
            .ThenBy(clip => (int?)clip.Attribute("columnIndex"))
            .Select(clip => (long?)clip.Attribute("uniqueId"));
        return savedClips.SequenceEqual(state.Layers.SelectMany(layer => layer.ClipIds).Select(id => (long?)id));
    }
}
