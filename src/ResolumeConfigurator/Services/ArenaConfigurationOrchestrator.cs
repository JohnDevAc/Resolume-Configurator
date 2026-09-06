using System.Xml.Linq;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public sealed class ArenaConfigurationOrchestrator
{
    public const int DefaultColumnCount = 20;
    public const int MinimumColumnCount = 5;
    public const int MaximumColumnCount = 50;
    public const int DefaultSourceStartColumn = 5;

    public async Task<ConfigurationResult> ConfigureAsync(ConfigurationPlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        var log = new List<string>();
        void Report(string message) { log.Add(message); progress?.Report(message); }
        using var api = new ResolumeApiClient();
        api.BeforeMutation = async token =>
        {
            var current = await JobRevisionGuard.RefreshAsync(plan.ConfiguratorUrl, plan.ExpectedJob, token);
            await LocalNdiReadinessService.ValidateAsync(current, token);
        };
        ValidatePlan(plan);
        var job = await JobRevisionGuard.RefreshAsync(plan.ConfiguratorUrl, plan.ExpectedJob, ct);
        await LocalNdiReadinessService.ValidateAsync(job, ct);
        var sourceColumnIndices = GetSourceColumnIndices(plan);
        var routerColumnIndex = GetRouterColumnIndex(plan);
        var product = await api.GetProductAsync(ct);
        if (!product.Name.Equals("Arena", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Resolume Arena is required; the webserver reported {product.Name}.");
        Report($"Connected to {product}.");
        await new KiloviewDecoderPresetService().ValidateConnectionsAsync(plan.Decoders, ct);
        Report($"Verified login and preset capacity for all {plan.Decoders.Count} decoders.");

        await JobRevisionGuard.RefreshAsync(plan.ConfiguratorUrl, plan.ExpectedJob, ct);
        Directory.CreateDirectory(plan.CompositionDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var groupNamesInBackToFrontOrder = plan.Decoders.Select(d => d.OutputName).Concat(new[] { "LED Wall", "Show" }).ToArray();
        var initial = await new ArenaCompositionSynchronizationService().EnsureCurrentAsync(api, plan.CompositionDirectory, progress, ct);
        await JobRevisionGuard.RefreshAsync(plan.ConfiguratorUrl, plan.ExpectedJob, ct);
        ValidateInitialComposition(initial, groupNamesInBackToFrontOrder.Length);
        var targetColumnCount = Math.Max(plan.TotalColumnCount, initial.ColumnIds.Count);
        await api.UpdateCompositionAsync(plan.CompositionName, plan.CompositionWidth, plan.CompositionHeight, ct);
        await api.GrowCompositionAsync(targetColumnCount, ct);
        var working = await api.GetCompositionStateAsync(ct);
        ValidateInitialComposition(working, groupNamesInBackToFrontOrder.Length);
        Report($"Overwriting the open composition at {plan.CompositionWidth} × {plan.CompositionHeight}; preserving its {initial.ColumnIds.Count} existing columns and ensuring {targetColumnCount} total.");

        var targetGroupIds = working.Groups.Select(group => group.Id).ToList();
        while (targetGroupIds.Count < groupNamesInBackToFrontOrder.Length)
        {
            var before = await api.GetCompositionStateAsync(ct);
            var knownIds = before.Groups.Select(group => group.Id).ToHashSet();
            await api.AddLayerGroupAsync(ct);
            var added = (await api.GetCompositionStateAsync(ct)).Groups.SingleOrDefault(group => !knownIds.Contains(group.Id))
                ?? throw new InvalidOperationException("Arena did not report the newly added layer group.");
            targetGroupIds.Add(added.Id);
        }

        for (var index = 0; index < targetGroupIds.Count; index++)
            await api.RenameGroupAsync(targetGroupIds[index], groupNamesInBackToFrontOrder[index], ct);

        foreach (var targetGroupId in targetGroupIds)
        {
            while (true)
            {
                var state = await api.GetCompositionStateAsync(ct);
                var targetGroup = state.Groups.Single(group => group.Id == targetGroupId);
                if (targetGroup.Layers.Count >= 3) break;

                var groupedIds = state.Groups.SelectMany(group => group.Layers).Select(layer => layer.Id).ToHashSet();
                var candidate = state.Layers.FirstOrDefault(layer => !groupedIds.Contains(layer.Id));
                candidate ??= state.Groups
                    .Where(group => group.Id != targetGroupId && targetGroupIds.Contains(group.Id) && group.Layers.Count > 3)
                    .SelectMany(group => group.Layers.Skip(3))
                    .FirstOrDefault();

                if (candidate is null)
                {
                    await api.AddLayerToGroupAsync(targetGroupId, ct);
                    continue;
                }

                var layerIndex = state.Layers.Select((layer, index) => (layer.Id, Index: index + 1))
                    .Single(layer => layer.Id == candidate.Id).Index;
                await api.MoveLayerToGroupAsync(targetGroupId, layerIndex, ct);
            }
        }

        var createdGroupIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        for (var groupIndex = 0; groupIndex < groupNamesInBackToFrontOrder.Length; groupIndex++)
        {
            var groupName = groupNamesInBackToFrontOrder[groupIndex];
            var refreshed = (await api.GetCompositionStateAsync(ct)).Groups.Single(group => group.Id == targetGroupIds[groupIndex]);
            if (refreshed.Layers.Count != 3) throw new InvalidOperationException($"Arena assigned {refreshed.Layers.Count} layers to {groupName}; expected 3.");

            var names = new[] { "Holding", "Secondary", "Primary" };
            for (var i = 0; i < 3; i++)
            {
                await api.ClearLayerClipsAsync(refreshed.Layers[i].Id, ct);
                await api.RenameLayerAsync(refreshed.Layers[i].Id, names[i], ct);
            }
            refreshed = (await api.GetCompositionStateAsync(ct)).Groups.Single(group => group.Id == targetGroupIds[groupIndex]);
            createdGroupIds[groupName] = refreshed.Id;
            Report($"Overwrote {groupName}: Holding, Secondary, Primary.");
        }

        // Group and layer creation is now complete. All routing is deliberately
        // resolved from this final state because Arena uses mutable 1-based indices.
        var finalStructure = await api.GetCompositionStateAsync(ct);
        var groupedLayerIds = finalStructure.Groups.SelectMany(group => group.Layers).Select(layer => layer.Id).ToHashSet();
        var ungroupedLayerCount = finalStructure.Layers.Count(layer => !groupedLayerIds.Contains(layer.Id));
        if (ungroupedLayerCount != 0)
            throw new InvalidOperationException($"Arena retained {ungroupedLayerCount} ungrouped starter layer(s).");
        var finalNames = finalStructure.Groups.Select(g => g.Name).ToArray();
        if (finalNames.Length < 2 || !finalNames[^1].Equals("Show", StringComparison.OrdinalIgnoreCase) ||
            !finalNames[^2].Equals("LED Wall", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Arena did not retain the required group order (Show top, LED Wall second).");

        var groupIndices = finalStructure.Groups
            .Select((group, index) => (group.Name, Index: index + 1))
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
        Report("Finalized every group and layer; resolving routes from Arena's final indices.");

        var sourceGroupNames = plan.Decoders.Select(decoder => decoder.OutputName).Append("Show").ToArray();
        if (plan.AutoPlaceNdiSources)
        {
            foreach (var groupName in sourceGroupNames)
            {
                var group = finalStructure.Groups.Single(g => g.Id == createdGroupIds[groupName]);
                foreach (var layer in group.Layers)
                {
                    if (layer.ClipIds.Count < targetColumnCount)
                        throw new InvalidOperationException($"Layer {layer.Name} does not expose all {targetColumnCount} requested columns.");
                    for (var encoder = 0; encoder < plan.Encoders.Count; encoder++)
                        await api.OpenSourceAsync(layer.ClipIds[sourceColumnIndices[encoder]], plan.Encoders[encoder].ArenaSourceToken, ct);
                }
                Report($"Filled encoder columns {plan.SourceStartColumn}–{plan.SourceStartColumn + plan.Encoders.Count - 1} in all three layers of {group.Name}.");
            }
        }
        else Report("Skipped automatic NDI source placement; all source slots remain available for manual setup.");

        foreach (var decoder in plan.Decoders)
        {
            var group = finalStructure.Groups.Single(g => g.Id == createdGroupIds[decoder.OutputName]);
            var primary = group.Layers.Single(l => l.Name.Equals("Primary", StringComparison.OrdinalIgnoreCase));
            var routerClipId = primary.ClipIds[routerColumnIndex];
            await api.OpenSourceAsync(routerClipId, "Video Router", ct);
            Report($"Loaded {group.Name} Primary column {routerColumnIndex + 1} Video Router.");
        }

        // Arena names a reloaded composition from its filename and can stall when
        // overwriting. Preserve any previous same-named file, then save to the exact
        // job filename so the live composition also carries the job name.
        var safeCompositionName = ArenaPaths.SafeFileName(plan.CompositionName, "NDI Job");
        await JobRevisionGuard.RefreshAsync(plan.ConfiguratorUrl, plan.ExpectedJob, ct);
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
        SetCollapsedLayerStates(compositionFile);

        // The current composition already has the job name. Give the live graph a
        // temporary marker so OpenCompositionAsync cannot mistake the old graph
        // for the reloaded, XML-patched composition merely because names match.
        await api.UpdateCompositionAsync($"Resolume Configurator Reloading {stamp}", plan.CompositionWidth, plan.CompositionHeight, ct);
        await api.OpenCompositionAsync(compositionFile, safeCompositionName, ct);
        await api.UpdateCompositionAsync(plan.CompositionName, plan.CompositionWidth, plan.CompositionHeight, ct);

        // Confirm that the patched composition is structurally complete before
        // writing Advanced Output and restarting Arena.
        var reloadedStructure = await api.GetCompositionStateAsync(ct);
        if (reloadedStructure.ColumnIds.Count != targetColumnCount)
            throw new InvalidOperationException($"Arena reloaded {reloadedStructure.ColumnIds.Count} columns; expected exactly {targetColumnCount}.");

        // Apply and verify every live clip state before the final save. These
        // settings are also restored by the fresh helper after restart because
        // Arena does not persist every live router setting in the AVC.
        var ndiClipIds = new List<long>();
        if (plan.AutoPlaceNdiSources)
        {
            foreach (var groupName in sourceGroupNames)
            {
                var group = reloadedStructure.Groups.Single(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
                foreach (var layer in group.Layers)
                {
                    foreach (var sourceColumnIndex in sourceColumnIndices)
                    {
                        var clipId = layer.ClipIds[sourceColumnIndex];
                        await api.ConfigureClipFitAsync(clipId, ct);
                        ndiClipIds.Add(clipId);
                    }
                }
            }
        }

        var routerClipIds = new List<long>();
        foreach (var decoder in plan.Decoders)
        {
            var group = reloadedStructure.Groups.Single(g => g.Name.Equals(decoder.OutputName, StringComparison.OrdinalIgnoreCase));
            var primary = group.Layers.Single(l => l.Name.Equals("Primary", StringComparison.OrdinalIgnoreCase));
            var routerClipId = primary.ClipIds[routerColumnIndex];
            await api.ConfigureVideoRouterFitAsync(routerClipId, ct);
            await api.ConnectClipAsync(routerClipId, ct);
            routerClipIds.Add(routerClipId);
        }

        if (ndiClipIds.Count > 0) await Task.Delay(500, ct);
        foreach (var clipId in ndiClipIds) await api.UpdateClipThumbnailAsync(clipId, ct);
        await api.SaveCompositionAsync(compositionFile, ct);
        Report($"Set {ndiClipIds.Count} decoder/Show NDI clips and {routerClipIds.Count} routers to Fit, enabled every decoder router, refreshed all NDI thumbnails, collapsed Secondary/Holding layers, and saved the routed {targetColumnCount}-column composition at {plan.FramesPerSecond} fps.");

        var generator = new AdvancedOutputPresetGenerator();
        await JobRevisionGuard.RefreshAsync(plan.ConfiguratorUrl, plan.ExpectedJob, ct);
        var preset = generator.Generate(plan, product, groupIndices);
        var presetFile = await generator.SaveAsync(preset, plan, ct);
        Report($"Wrote Advanced Output preset {Path.GetFileName(presetFile)}.");

        var expectedScreens = new[] { "Show", "LED Wall" }.Concat(plan.Decoders.Select(decoder => decoder.OutputName)).ToArray();
        var arenaPaths = ArenaPaths.Resolve();
        var activator = new AdvancedOutputActivator();
        await activator.ActivateAsync(presetFile, arenaPaths.AdvancedOutputPreference, expectedScreens, ct);
        Report($"Updated Arena's active Advanced Output XML with {expectedScreens.Length} outputs (no window automation).");

        await new SimpleOutputConfigurationService().ApplyNdiCompositionSharingAsync(
            arenaPaths.SimpleOutputPreference,
            plan.EnableNdiCompositionSharing,
            plan.CompositionWidth,
            plan.CompositionHeight,
            ct);
        Report($"NDI composition sharing will be {(plan.EnableNdiCompositionSharing ? "enabled" : "disabled")} from Resolume's persisted SimpleOutput.xml after restart.");

        // Close every connection to the current Arena webserver before its
        // process is replaced. Keeping the pre-restart HttpClient alive can
        // leave a localhost request bound to the retired listener even after
        // the new Arena server is responding to other processes.
        api.Dispose();
        await new ArenaRestartService().RestartAsync(compositionFile, plan.CompositionName, progress, ct,
            plan.SourceStartColumn, plan.AutoPlaceNdiSources ? plan.Encoders.Count : 0, plan.ConfiguratorUrl, plan.ExpectedJob);
        Report("Arena restart initiated. Decoder activation will report back in the main window.");
        return new ConfigurationResult(compositionFile, presetFile, null, log, []);
    }

    public static IReadOnlyList<int> GetSourceColumnIndices(ConfigurationPlan plan) =>
        plan.AutoPlaceNdiSources
            ? Enumerable.Range(plan.SourceStartColumn - 1, plan.Encoders.Count).ToArray()
            : [];

    public static int GetRouterColumnIndex(ConfigurationPlan plan) =>
        plan.AutoPlaceNdiSources ? plan.SourceStartColumn - 1 + plan.Encoders.Count : 0;

    public static int GetMinimumColumnCountForPlacement(int sourceStartColumn, int sourceCount, bool autoPlaceNdiSources) =>
        autoPlaceNdiSources
            ? Math.Clamp(sourceStartColumn + sourceCount, MinimumColumnCount, MaximumColumnCount)
            : MinimumColumnCount;

    public static void ValidatePlan(ConfigurationPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.CompositionName))
            throw new InvalidOperationException("Composition name is empty.");
        if (plan.CompositionWidth is < 320 or > 32768 || plan.CompositionHeight is < 240 or > 32768)
            throw new InvalidOperationException("The composition resolution is invalid.");
        if (plan.Decoders.Count == 0)
            throw new InvalidOperationException("No onboarded decoders were found.");
        if (plan.Decoders.Any(decoder => decoder.Width is < 320 or > 32768 || decoder.Height is < 240 or > 32768))
            throw new InvalidOperationException("A decoder resolution is invalid.");
        if (plan.Decoders.Any(decoder => decoder.Device.Credentials is null))
            throw new InvalidOperationException("Saved decoder credentials are missing from NDI Job Configurator.");
        var groupNames = plan.Decoders.Select(decoder => decoder.OutputName.Trim()).Concat(new[] { "LED Wall", "Show" }).ToArray();
        if (groupNames.Any(string.IsNullOrWhiteSpace) || groupNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != groupNames.Length)
            throw new InvalidOperationException("Decoder names must be nonempty, unique, and cannot be named Show or LED Wall.");
        if (plan.AutoPlaceNdiSources && (plan.Encoders.Count == 0 || plan.Encoders.Any(encoder => string.IsNullOrWhiteSpace(encoder.ArenaSourceToken))))
            throw new InvalidOperationException("Automatic placement requires at least one encoder and a matched source for every encoder.");
        if (plan.TotalColumnCount is < MinimumColumnCount or > MaximumColumnCount)
            throw new InvalidOperationException($"Column count must be between {MinimumColumnCount} and {MaximumColumnCount}.");
        if (plan.SourceStartColumn is < 1 || plan.SourceStartColumn > plan.TotalColumnCount)
            throw new InvalidOperationException($"Source starting column must be between 1 and {plan.TotalColumnCount}.");
        if (plan.FramesPerSecond is not (50 or 60))
            throw new InvalidOperationException("Frame rate must be 50 or 60 fps.");
        if (GetRouterColumnIndex(plan) >= plan.TotalColumnCount)
            throw new InvalidOperationException($"The selected source starting column and {plan.Encoders.Count} encoder feeds leave no column for the Video Router.");
    }

    internal static void ValidateInitialComposition(ArenaCompositionState state, int targetGroupCount)
    {
        if (state.Groups.Count > targetGroupCount)
            throw new InvalidOperationException($"The open composition has {state.Groups.Count} groups but this job requires {targetGroupCount}; Arena 7.27 cannot delete the surplus groups through its REST API.");
        if (state.Layers.Count > targetGroupCount * 3)
            throw new InvalidOperationException($"The open composition has {state.Layers.Count} layers but this job uses {targetGroupCount * 3}; Arena 7.27 cannot delete the surplus layers through its REST API.");
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

    public static void SetCollapsedLayerStates(string path)
    {
        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        foreach (var layer in document.Root?.Elements("Layer") ?? [])
        {
            var layerName = layer.Elements("Params")
                .SelectMany(parameters => parameters.Elements("Param"))
                .FirstOrDefault(parameter => (string?)parameter.Attribute("name") == "Name")?
                .Attribute("value")?.Value;
            if (layerName is null) continue;

            var layerView = layer.Element("LayerView");
            if (layerView is null)
            {
                layerView = new XElement("LayerView", new XAttribute("name", "LayerView"));
                layer.Add(layerView);
            }
            layerView.SetAttributeValue("foldedControl",
                layerName.Equals("Holding", StringComparison.OrdinalIgnoreCase) ||
                layerName.Equals("Secondary", StringComparison.OrdinalIgnoreCase) ? "1" : "0");
        }
        document.Save(path, SaveOptions.DisableFormatting);
    }
}
