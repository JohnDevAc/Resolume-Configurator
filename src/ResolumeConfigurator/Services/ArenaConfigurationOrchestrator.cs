using System.Xml.Linq;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public sealed class ArenaConfigurationOrchestrator
{
    public const int TotalColumnCount = 20;

    public async Task<ConfigurationResult> ConfigureAsync(ConfigurationPlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        var log = new List<string>();
        void Report(string message) { log.Add(message); progress?.Report(message); }
        using var api = new ResolumeApiClient();
        var product = await api.GetProductAsync(ct);
        if (!product.Name.Equals("Arena", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Resolume Arena is required; the webserver reported {product.Name}.");
        Report($"Connected to {product}.");

        Directory.CreateDirectory(plan.CompositionDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var backup = Path.Combine(plan.CompositionDirectory, $"Before Resolume Configurator {stamp}.avc");
        await api.SaveCompositionAsync(backup, ct);
        Report($"Backed up the current composition to {Path.GetFileName(backup)}.");

        var existing = await api.GetCompositionStateAsync(ct);
        foreach (var group in existing.Groups.Reverse()) await api.DeleteGroupAsync(group.Id, ct);

        var ungrouped = await api.GetCompositionStateAsync(ct);
        if (ungrouped.Layers.Count == 0)
            throw new InvalidOperationException("Arena did not retain a temporary layer while resetting the composition.");
        var starterLayerId = ungrouped.Layers[0].Id;
        foreach (var layer in ungrouped.Layers.Skip(1).Reverse()) await api.DeleteLayerAsync(layer.Id, ct);

        if (plan.Encoders.Count + 1 > TotalColumnCount)
            throw new InvalidOperationException($"The job has {plan.Encoders.Count} encoder feeds, but a {TotalColumnCount}-column composition must retain one column for the Video Router.");
        const int targetColumnCount = TotalColumnCount;
        var resetState = await api.GetCompositionStateAsync(ct);
        foreach (var columnId in resetState.ColumnIds.Skip(targetColumnCount).Reverse()) await api.DeleteColumnAsync(columnId, ct);

        await api.UpdateCompositionAsync(plan.CompositionName, plan.CompositionWidth, plan.CompositionHeight, ct);
        await api.GrowCompositionAsync(targetColumnCount, ct);
        var initial = await api.GetCompositionStateAsync(ct);
        if (initial.Groups.Count != 0 || initial.Layers.Count != 1 || initial.Layers[0].Id != starterLayerId)
            throw new InvalidOperationException("Arena did not reach the expected clean composition state.");
        Report($"Reset the composition to {plan.CompositionWidth} × {plan.CompositionHeight} with {targetColumnCount} columns.");

        var groupNamesInBackToFrontOrder = plan.Decoders.Select(d => d.OutputName).Concat(new[] { "LED Wall", "Show" }).ToArray();
        if (groupNamesInBackToFrontOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != groupNamesInBackToFrontOrder.Length)
            throw new InvalidOperationException("Decoder names must be unique and cannot be named Show or LED Wall.");

        var createdGroupIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var groupName in groupNamesInBackToFrontOrder)
        {
            var before = await api.GetCompositionStateAsync(ct);
            var knownIds = before.Groups.Select(g => g.Id).ToHashSet();
            await api.AddLayerGroupAsync(ct);
            var after = await api.GetCompositionStateAsync(ct);
            var group = after.Groups.SingleOrDefault(g => !knownIds.Contains(g.Id))
                ?? throw new InvalidOperationException("Arena did not report the newly created layer group.");

            await api.RenameGroupAsync(group.Id, groupName, ct);
            await api.AddLayerToGroupAsync(group.Id, ct);
            await api.AddLayerToGroupAsync(group.Id, ct);
            var refreshed = (await api.GetCompositionStateAsync(ct)).Groups.Single(g => g.Id == group.Id);
            if (refreshed.Layers.Count != 3) throw new InvalidOperationException($"Arena created {refreshed.Layers.Count} layers in {groupName}; expected 3.");

            var names = new[] { "Holding", "Secondary", "Primary" };
            for (var i = 0; i < 3; i++) await api.RenameLayerAsync(refreshed.Layers[i].Id, names[i], ct);
            refreshed = (await api.GetCompositionStateAsync(ct)).Groups.Single(g => g.Id == group.Id);
            createdGroupIds[groupName] = refreshed.Id;
            Report($"Created {groupName}: Holding, Secondary, Primary.");
        }

        await api.DeleteLayerAsync(starterLayerId, ct);

        // Group and layer creation is now complete. All routing is deliberately
        // resolved from this final state because Arena uses mutable 1-based indices.
        var finalStructure = await api.GetCompositionStateAsync(ct);
        var finalNames = finalStructure.Groups.Select(g => g.Name).ToArray();
        if (finalNames.Length < 2 || !finalNames[^1].Equals("Show", StringComparison.OrdinalIgnoreCase) ||
            !finalNames[^2].Equals("LED Wall", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Arena did not retain the required group order (Show top, LED Wall second).");

        var groupIndices = finalStructure.Groups
            .Select((group, index) => (group.Name, Index: index + 1))
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
        Report("Finalized every group and layer; resolving routes from Arena's final indices.");

        var sourceGroupNames = plan.Decoders.Select(decoder => decoder.OutputName).Append("Show").ToArray();
        foreach (var groupName in sourceGroupNames)
        {
            var group = finalStructure.Groups.Single(g => g.Id == createdGroupIds[groupName]);
            foreach (var layer in group.Layers)
            {
                if (layer.ClipIds.Count < plan.Encoders.Count + 1)
                    throw new InvalidOperationException($"Layer {layer.Name} does not have enough columns for encoder sources and routing.");
                for (var column = 0; column < plan.Encoders.Count; column++)
                    await api.OpenSourceAsync(layer.ClipIds[column], plan.Encoders[column].ArenaSourceToken, ct);
            }
            Report($"Filled encoder columns in all three layers of {group.Name}.");
        }

        foreach (var decoder in plan.Decoders)
        {
            var group = finalStructure.Groups.Single(g => g.Id == createdGroupIds[decoder.OutputName]);
            var primary = group.Layers.Single(l => l.Name.Equals("Primary", StringComparison.OrdinalIgnoreCase));
            var routerClipId = primary.ClipIds[plan.Encoders.Count];
            await api.OpenSourceAsync(routerClipId, "Video Router", ct);
            Report($"Loaded {group.Name} Primary column {plan.Encoders.Count + 1} Video Router.");
        }

        // Arena names a reloaded composition from its filename and can stall when
        // overwriting. Preserve any previous same-named file, then save to the exact
        // job filename so the live composition also carries the job name.
        var safeCompositionName = ArenaPaths.SafeFileName(plan.CompositionName, "NDI Job");
        var compositionFile = Path.Combine(plan.CompositionDirectory, $"{safeCompositionName}.avc");
        if (File.Exists(compositionFile))
        {
            var archivedComposition = Path.Combine(plan.CompositionDirectory, $"{safeCompositionName} Previous {stamp}.avc");
            File.Move(compositionFile, archivedComposition);
            Report($"Archived the previous same-named composition as {Path.GetFileName(archivedComposition)}.");
        }
        await api.SaveCompositionAsync(compositionFile, ct);
        SetCompositionFrameRate(compositionFile, plan.FramesPerSecond);
        SetVideoRouterInputs(compositionFile, groupIndices["Show"], plan.Decoders.Count);

        // The current composition already has the job name. Give the live graph a
        // temporary marker so OpenCompositionAsync cannot mistake the old graph
        // for the reloaded, XML-patched composition merely because names match.
        await api.UpdateCompositionAsync($"Resolume Configurator Reloading {stamp}", plan.CompositionWidth, plan.CompositionHeight, ct);
        await api.OpenCompositionAsync(compositionFile, safeCompositionName, ct);
        await api.UpdateCompositionAsync(plan.CompositionName, plan.CompositionWidth, plan.CompositionHeight, ct);

        // Confirm that the patched composition is structurally complete before
        // writing Advanced Output and restarting Arena.
        var reloadedStructure = await api.GetCompositionStateAsync(ct);
        if (reloadedStructure.ColumnIds.Count != TotalColumnCount)
            throw new InvalidOperationException($"Arena reloaded {reloadedStructure.ColumnIds.Count} columns; expected exactly {TotalColumnCount}.");

        // Apply and verify every live clip state before the final save. These
        // settings and refreshed thumbnails are retained in the AVC across the
        // Arena restart, so they must not be redundantly driven through the API
        // from the process that launched the replacement Arena instance.
        var ndiClipIds = new List<long>();
        foreach (var groupName in sourceGroupNames)
        {
            var group = reloadedStructure.Groups.Single(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
            foreach (var layer in group.Layers)
            {
                for (var column = 0; column < plan.Encoders.Count; column++)
                {
                    var clipId = layer.ClipIds[column];
                    await api.ConfigureClipFitAsync(clipId, ct);
                    ndiClipIds.Add(clipId);
                }
            }
        }

        var routerClipIds = new List<long>();
        foreach (var decoder in plan.Decoders)
        {
            var group = reloadedStructure.Groups.Single(g => g.Name.Equals(decoder.OutputName, StringComparison.OrdinalIgnoreCase));
            var primary = group.Layers.Single(l => l.Name.Equals("Primary", StringComparison.OrdinalIgnoreCase));
            var routerClipId = primary.ClipIds[plan.Encoders.Count];
            await api.ConfigureVideoRouterFitAsync(routerClipId, ct);
            await api.ConnectClipAsync(routerClipId, ct);
            routerClipIds.Add(routerClipId);
        }

        if (ndiClipIds.Count > 0) await Task.Delay(500, ct);
        foreach (var clipId in ndiClipIds) await api.UpdateClipThumbnailAsync(clipId, ct);
        await api.SaveCompositionAsync(compositionFile, ct);
        Report($"Set {ndiClipIds.Count} decoder/Show NDI clips and {routerClipIds.Count} routers to Fit, enabled every decoder router, refreshed all NDI thumbnails, and saved the routed {TotalColumnCount}-column composition at {plan.FramesPerSecond} fps.");

        var generator = new AdvancedOutputPresetGenerator();
        var preset = generator.Generate(plan, product, groupIndices);
        var presetFile = await generator.SaveAsync(preset, plan, ct);
        Report($"Wrote Advanced Output preset {Path.GetFileName(presetFile)}.");

        var expectedScreens = new[] { "Show", "LED Wall" }.Concat(plan.Decoders.Select(decoder => decoder.OutputName)).ToArray();
        var activator = new AdvancedOutputActivator();
        await activator.ActivateAsync(presetFile, ArenaPaths.Resolve().AdvancedOutputPreference, expectedScreens, ct);
        Report($"Updated Arena's active Advanced Output XML with {expectedScreens.Length} outputs (no window automation).");

        // Close every connection to the current Arena webserver before its
        // process is replaced. Keeping the pre-restart HttpClient alive can
        // leave a localhost request bound to the retired listener even after
        // the new Arena server is responding to other processes.
        api.Dispose();
        await new ArenaRestartService().RestartAsync(compositionFile, safeCompositionName, progress, ct);
        Report("Arena restart initiated. Decoder activation will report back in the main window.");
        return new ConfigurationResult(compositionFile, presetFile, backup, log, []);
    }

    public static void SetCompositionFrameRate(string path, int framesPerSecond)
    {
        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var videoParams = document.Root?.Elements("VideoTrack").FirstOrDefault()?.Element("Params")
            ?? throw new InvalidDataException("The saved composition does not contain its VideoTrack parameters.");
        var frameRate = videoParams.Elements("ParamRange").FirstOrDefault(e => (string?)e.Attribute("name") == "FrameRate");
        if (frameRate is null)
        {
            frameRate = new XElement("ParamRange",
                new XAttribute("name", "FrameRate"), new XAttribute("T", "DOUBLE"), new XAttribute("default", "0"), new XAttribute("value", framesPerSecond),
                new XElement("PhaseSourceStatic", new XAttribute("name", "PhaseSourceStatic")));
            videoParams.Add(frameRate);
        }
        else frameRate.SetAttributeValue("value", framesPerSecond);
        document.Save(path, SaveOptions.DisableFormatting);
    }

    public static void SetVideoRouterInputs(string path, int showGroupIndex, int expectedRouterCount)
    {
        if (showGroupIndex < 1) throw new ArgumentOutOfRangeException(nameof(showGroupIndex));
        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var routers = document.Descendants("VideoSource")
            .Where(e => (string?)e.Attribute("type") == "CompositionRouterVideoSource")
            .ToArray();
        if (routers.Length != expectedRouterCount)
            throw new InvalidDataException($"The saved composition contains {routers.Length} Video Routers; expected {expectedRouterCount}.");

        foreach (var router in routers)
        {
            var settings = router.Elements("Params").FirstOrDefault(e => (string?)e.Attribute("name") == "Settings");
            if (settings is null)
            {
                settings = new XElement("Params", new XAttribute("name", "Settings"));
                router.Add(settings);
            }
            var input = settings.Elements("ParamChoice").FirstOrDefault(e => (string?)e.Attribute("name") == "Input");
            if (input is null)
            {
                input = new XElement("ParamChoice", new XAttribute("name", "Input"));
                settings.Add(input);
            }
            input.SetAttributeValue("default", "0:0");
            input.SetAttributeValue("value", $"1:{showGroupIndex}");
            input.SetAttributeValue("storeChoices", "0");
        }
        document.Save(path, SaveOptions.DisableFormatting);
    }
}
