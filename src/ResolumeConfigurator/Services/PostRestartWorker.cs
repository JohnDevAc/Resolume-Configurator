using System.Net.Http;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public sealed class PostRestartWorker
{
    public async Task<IReadOnlyList<DecoderPresetResult>> RunAsync(string expectedJobName, CancellationToken ct,
        PostRestartComposition? restoration = null, string? configuratorUrl = null, JobIdentity? expectedJob = null)
    {
        // This is a fresh process launched immediately after replacement Arena.
        // Give Arena a full 15 seconds to load Advanced Output and publish its
        // NDI senders before the Kiloview discovery requests begin.
        await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

        var snapshot = await JobRevisionGuard.RefreshAsync(configuratorUrl, expectedJob, ct).ConfigureAwait(false);
        if (!snapshot.JobName.Trim().Equals(expectedJobName.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"NDI Job Configurator changed from '{expectedJobName}' to '{snapshot.JobName}' during Arena restart.");

        var decoderDevices = snapshot.Devices
            .Where(device => device.IsOnboarded && device.Role.Equals("Decoder", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var decoders = decoderDevices.Select((device, index) =>
        {
            var detected = ResolutionParser.TryParse(device.HdmiOutputResolution, out var width, out var height);
            return new DecoderRow
            {
                Order = index + 1, Device = device, OutputName = device.Hostname,
                Width = detected ? width : 1920, Height = detected ? height : 1080,
                UsedFallbackResolution = !detected
            };
        }).ToArray();

        if (decoders.Length == 0) throw new InvalidOperationException("No onboarded Kiloview decoders were available after Arena restart.");
        if (decoders.Any(decoder => decoder.Device.Credentials is null))
            throw new InvalidOperationException("Saved decoder credentials are missing from NDI Job Configurator.");

        if (restoration is not null)
            await RestoreCompositionAsync(expectedJobName, decoders.Select(decoder => decoder.OutputName).ToArray(), restoration, ct).ConfigureAwait(false);

        await JobRevisionGuard.RefreshAsync(configuratorUrl, expectedJob, ct).ConfigureAwait(false);
        return await new KiloviewDecoderPresetService().ConfigureAsync(decoders, null, ct,
            async token => { await JobRevisionGuard.RefreshAsync(configuratorUrl, expectedJob, token).ConfigureAwait(false); }).ConfigureAwait(false);
    }

    public async Task RestoreCompositionAsync(string expectedJobName, IReadOnlyList<string> decoderNames,
        PostRestartComposition restoration, CancellationToken ct)
    {
        using var api = new ResolumeApiClient(timeout: TimeSpan.FromSeconds(8));
        ArenaCompositionState? state = null;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var filename = Path.GetFileNameWithoutExtension(restoration.CompositionFile);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var candidate = await api.GetCompositionStateAsync(ct).ConfigureAwait(false);
                if (candidate.Name.Equals(filename, StringComparison.OrdinalIgnoreCase)
                    || candidate.Name.Equals(expectedJobName, StringComparison.OrdinalIgnoreCase))
                {
                    state = candidate;
                    break;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException or OperationCanceledException)) { }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        if (state is null) throw new InvalidOperationException("Arena did not reopen the saved job composition after restart.");

        var clips = ResolveRestorationClips(state, decoderNames, restoration.SourceStartColumn, restoration.SourceCount);
        foreach (var clipId in clips.NdiClipIds) await api.ConfigureClipFitAsync(clipId, ct).ConfigureAwait(false);
        foreach (var clipId in clips.RouterClipIds)
        {
            await api.ConfigureVideoRouterFitAsync(clipId, ct).ConfigureAwait(false);
            await api.ConnectClipAsync(clipId, ct).ConfigureAwait(false);
        }
        foreach (var clipId in clips.NdiClipIds) await api.UpdateClipThumbnailAsync(clipId, ct).ConfigureAwait(false);
        await api.SaveCompositionAsync(restoration.CompositionFile, ct).ConfigureAwait(false);
    }

    internal static (IReadOnlyList<long> NdiClipIds, IReadOnlyList<long> RouterClipIds) ResolveRestorationClips(
        ArenaCompositionState state, IReadOnlyList<string> decoderNames, int sourceStartColumn, int sourceCount)
    {
        if (sourceStartColumn < 1 || sourceCount < 0) throw new InvalidOperationException("Invalid post-restart source placement.");
        var expectedNames = decoderNames.Concat(new[] { "LED Wall", "Show" }).ToArray();
        if (!state.Groups.Select(group => group.Name).SequenceEqual(expectedNames, StringComparer.OrdinalIgnoreCase)
            || state.Groups.Any(group => group.Layers.Count != 3) || state.Layers.Count != expectedNames.Length * 3)
            throw new InvalidOperationException("Arena's restarted composition does not contain the expected job groups and layers.");
        var ndi = new List<long>();
        var routers = new List<long>();
        foreach (var group in state.Groups.Where(group => !group.Name.Equals("LED Wall", StringComparison.OrdinalIgnoreCase)))
        {
            var routerIndex = sourceCount == 0 ? 0 : sourceStartColumn - 1 + sourceCount;
            if (group.Layers.Any(layer => layer.ClipIds.Count <= routerIndex))
                throw new InvalidOperationException("Arena's restarted layers do not expose the expected source and router columns.");
            if (sourceCount > 0)
                foreach (var layer in group.Layers) ndi.AddRange(layer.ClipIds.Skip(sourceStartColumn - 1).Take(sourceCount));
            if (!group.Name.Equals("Show", StringComparison.OrdinalIgnoreCase))
                routers.Add(group.Layers.Single(layer => layer.Name.Equals("Primary", StringComparison.OrdinalIgnoreCase)).ClipIds[routerIndex]);
        }
        return (ndi, routers);
    }
}

public sealed record PostRestartComposition(string CompositionFile, int SourceStartColumn, int SourceCount);
