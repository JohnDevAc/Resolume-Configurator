using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public sealed class PostRestartWorker
{
    public async Task<IReadOnlyList<DecoderPresetResult>> RunAsync(string expectedJobName, CancellationToken ct)
    {
        // This is a fresh process launched immediately after replacement Arena.
        // Give Arena a full 15 seconds to load Advanced Output and publish its
        // NDI senders before the Kiloview discovery requests begin.
        await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

        var snapshot = await new NdiJobConfiguratorReader().ReadAsync(ct).ConfigureAwait(false);
        if (!snapshot.JobName.Equals(expectedJobName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"NDI Job Configurator changed from '{expectedJobName}' to '{snapshot.JobName}' during Arena restart.");

        var decoderDevices = snapshot.Devices
            .Where(device => device.IsOnboarded && device.Role.Equals("Decoder", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var decoders = decoderDevices.Select((device, index) => new DecoderRow
        {
            Order = index + 1,
            Device = device,
            OutputName = device.Hostname,
            Width = ResolutionParser.TryParse(device.HdmiOutputResolution, out var width, out _) ? width : 1920,
            Height = ResolutionParser.TryParse(device.HdmiOutputResolution, out _, out var height) ? height : 1080,
            UsedFallbackResolution = !ResolutionParser.TryParse(device.HdmiOutputResolution, out _, out _)
        }).ToArray();

        if (decoders.Length == 0) throw new InvalidOperationException("No onboarded Kiloview decoders were available after Arena restart.");
        if (decoders.Any(decoder => decoder.Device.Credentials is null))
            throw new InvalidOperationException("Saved decoder credentials are missing from NDI Job Configurator.");

        return await new KiloviewDecoderPresetService().ConfigureAsync(decoders, null, ct).ConfigureAwait(false);
    }
}
