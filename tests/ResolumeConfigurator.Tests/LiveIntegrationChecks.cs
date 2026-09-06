using System.IO;
using System.Text.Json;
using ResolumeConfigurator.Models;
using ResolumeConfigurator.Services;

internal static class LiveIntegrationChecks
{
    public static async Task<int> InspectFleetAsync(string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var snapshot = await new NdiJobConfiguratorReader().ReadAsync(deadline.Token);
        var diagnostics = new List<object>();
        var service = new KiloviewDecoderPresetService();
        var requestedDecoder = args.SkipWhile(argument => argument != "--decoder").Skip(1).FirstOrDefault();
        var failed = false;
        foreach (var device in snapshot.Devices.Where(device => device.IsOnboarded && device.Role.Equals("Decoder", StringComparison.OrdinalIgnoreCase)
            && (requestedDecoder is null || device.Hostname.Equals(requestedDecoder, StringComparison.OrdinalIgnoreCase))))
        {
            var decoder = new DecoderRow { Order = 1, Device = device, OutputName = device.Hostname, Width = 1920, Height = 1080 };
            try
            {
                object diagnostic = device.Family.Contains("N60", StringComparison.OrdinalIgnoreCase)
                    ? await service.InspectN60Async(decoder, deadline.Token)
                    : await service.InspectN6Async(decoder, deadline.Token);
                diagnostics.Add(diagnostic);
            }
            catch (Exception ex)
            {
                failed = true;
                diagnostics.Add(new { DecoderName = device.Hostname, Error = ex.Message });
            }
        }
        var json = JsonSerializer.Serialize(new { snapshot.JobName, Decoders = diagnostics }, new JsonSerializerOptions { WriteIndented = true });
        var output = args.SkipWhile(argument => argument != "--inspect-fleet").Skip(1).FirstOrDefault();
        if (output is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, json, deadline.Token);
        }
        Console.WriteLine(json);
        return failed ? 1 : 0;
    }
}
