using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
        var adapters = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses
            .Select(address => new LocalAdapterAddress(n.Id, address.Address.ToString(), address.PrefixLength,
                n.OperationalStatus == OperationalStatus.Up && address.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)));
        var selected = ValidateLocalState(state.RootElement, adapters);
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync($"http://{selected.Address}:8094/api/v1/status", ct);
        response.EnsureSuccessStatusCode();
        using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        ValidateStatusIdentity(status.RootElement, selected);
        Validate(status.RootElement, selected.EndpointId, job);
    }

    internal sealed record LocalAdapterAddress(string AdapterId, string Address, int Prefix, bool Ready);
    internal sealed record LocalAgentIdentity(string EndpointId, string AdapterId, string Address, int Prefix);

    internal static LocalAgentIdentity ValidateLocalState(JsonElement state, IEnumerable<LocalAdapterAddress> adapters)
    {
        var endpoint = String(state, "endpointId");
        var adapter = String(state, "adapterId");
        var address = String(state, "address");
        var prefix = Number(state, "prefixLength");
        if (Number(state, "schemaVersion") != 1 || !Guid.TryParse(endpoint, out var endpointGuid) || endpointGuid == Guid.Empty
            || !Guid.TryParse(adapter, out var adapterGuid) || adapterGuid == Guid.Empty || prefix is < 1 or > 30
            || !IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)
            || ip.GetAddressBytes()[0] == 0 || ip.GetAddressBytes()[0] >= 224 || ip.GetAddressBytes()[0] == 169 && ip.GetAddressBytes()[1] == 254
            || !adapters.Any(a => a.Ready && Guid.TryParse(a.AdapterId, out var id) && id == adapterGuid
                && a.Prefix == prefix && IPAddress.TryParse(a.Address, out var actual) && actual.Equals(ip)))
            throw new InvalidOperationException("The PC Agent's saved identity or selected adapter/address is invalid or unavailable. Refresh or repair PC Agent before configuring Arena.");
        return new(endpoint!, adapter!, ip.ToString(), prefix);
    }

    internal static void ValidateStatusIdentity(JsonElement status, LocalAgentIdentity selected)
    {
        if (Number(status, "schemaVersion") != 1
            || !string.Equals(String(status, "endpointId"), selected.EndpointId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(String(status, "adapterId"), selected.AdapterId, StringComparison.OrdinalIgnoreCase)
            || String(status, "address") != selected.Address || Number(status, "prefixLength") != selected.Prefix)
            throw new InvalidOperationException("The responding PC Agent does not match the saved endpoint and production adapter. Refresh PC Agent before configuring Arena.");
    }

    private static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static int Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number) ? number : 0;

    public static void Validate(JsonElement status, string? endpoint, JobSnapshot job)
    {
        static bool Group(JsonElement ndi, string name, string expected) => ndi.TryGetProperty(name, out var groups)
            && groups.ValueKind == JsonValueKind.Array && groups.EnumerateArray().Any(g => g.ValueKind == JsonValueKind.String && g.GetString() == expected);
        if (String(status, "endpointId") != endpoint
            || !status.TryGetProperty("ndiConfiguration", out var ndi) || ndi.ValueKind != JsonValueKind.Object
            || !ndi.TryGetProperty("preferredInterfaceConfigured", out var preferred) || preferred.ValueKind != JsonValueKind.True
            || !Group(ndi, "sendGroups", job.JobName) || !Group(ndi, "receiveGroups", job.JobName)
            || string.IsNullOrWhiteSpace(job.DiscoveryServer) || !ndi.TryGetProperty("discoveryServer", out var discovery)
            || discovery.ValueKind != JsonValueKind.String || discovery.GetString() != job.DiscoveryServer)
            throw new InvalidOperationException("This PC's NDI interface, job groups or Discovery Server do not match the selected job. Reapply PC Agent onboarding before changing Arena.");
    }
}
