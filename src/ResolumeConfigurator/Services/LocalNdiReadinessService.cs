using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public static class LocalNdiReadinessService
{
    public static async Task ValidateAsync(JobSnapshot job, CancellationToken ct)
    {
        var path = Environment.GetEnvironmentVariable("RESOLUME_PC_AGENT_STATE_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NDI Configurator", "PC Agent", "agent-state.json");
        if (!File.Exists(path)) throw new InvalidOperationException("Onboard this Arena PC into the selected job using PC Agent before changing Arena. Job Configurator and Discovery can remain remote.");
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        var address = state.RootElement.GetProperty("address").GetString();
        var endpoint = state.RootElement.GetProperty("endpointId").GetString();
        if (!IPAddress.TryParse(address, out var ip) || IPAddress.IsLoopback(ip)
            || !NetworkInterface.GetAllNetworkInterfaces().Any(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(ip))))
            throw new InvalidOperationException("The PC Agent's address is stale. Refresh or repair the selected production adapter before configuring Arena.");
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync($"http://{ip}:8094/api/v1/status", ct);
        response.EnsureSuccessStatusCode();
        using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Validate(status.RootElement, endpoint, job);
    }

    public static void Validate(JsonElement status, string? endpoint, JobSnapshot job)
    {
        static bool Group(JsonElement ndi, string name, string expected) => ndi.TryGetProperty(name, out var groups)
            && groups.ValueKind == JsonValueKind.Array && groups.EnumerateArray().Any(g => g.GetString() == expected);
        if (status.GetProperty("endpointId").GetString() != endpoint
            || !status.TryGetProperty("ndiConfiguration", out var ndi) || ndi.ValueKind != JsonValueKind.Object
            || !ndi.TryGetProperty("preferredInterfaceConfigured", out var preferred) || preferred.ValueKind != JsonValueKind.True
            || !Group(ndi, "sendGroups", job.JobName) || !Group(ndi, "receiveGroups", job.JobName)
            || string.IsNullOrWhiteSpace(job.DiscoveryServer) || !ndi.TryGetProperty("discoveryServer", out var discovery)
            || discovery.GetString() != job.DiscoveryServer)
            throw new InvalidOperationException("This PC's NDI interface, job groups or Discovery Server do not match the selected job. Reapply PC Agent onboarding before changing Arena.");
    }
}
