using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public sealed class NdiJobConfiguratorReader
{
    private const int Port = 8091;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public string ResolveStatePath()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("NDI_JOB_CONFIGURATOR_DATA_DIR")
            ?? Environment.GetEnvironmentVariable("KILOVIEW_DATA_DIR");
        var directory = !string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.GetFullPath(overrideDirectory)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NDI Job Configurator");
        return Path.Combine(directory, "state.json");
    }

    public async Task<JobSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var configuredUrl = Environment.GetEnvironmentVariable("NDI_JOB_CONFIGURATOR_URL");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredUrl)) candidates.Add(configuredUrl);
        candidates.Add($"http://127.0.0.1:{Port}");

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = await TryReadApiAsync(candidate, cancellationToken);
            if (snapshot is not null) return await MergeLocalCredentialsAsync(snapshot, cancellationToken);
        }

        var discovered = await DiscoverOnLanAsync(cancellationToken);
        if (discovered is not null) return await MergeLocalCredentialsAsync(discovered, cancellationToken);

        var path = ResolveStatePath();
        var document = await ReadDocumentAsync(path, cancellationToken)
            ?? await ReadDocumentAsync(path + ".bak", cancellationToken)
            ?? throw new FileNotFoundException($"NDI Job Configurator was not found on TCP {Port} or in its local state folder.", path);

        using (document) return ParseSnapshot(document.RootElement, path, includeCredentials: true);
    }

    private static JobSnapshot ParseSnapshot(JsonElement root, string source, bool includeCredentials = false)
    {
        var jobName = GetString(root, "lastJob", "jobName") ?? "NDI Job";
        var devices = new List<JobDevice>();
        if (root.TryGetProperty("devices", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                devices.Add(new JobDevice(
                    GetString(item, "id") ?? "",
                    GetString(item, "ipAddress") ?? "",
                    GetString(item, "hostname") ?? "Unnamed device",
                    GetString(item, "model") ?? "Unknown",
                    GetString(item, "family") ?? "Unknown",
                    GetString(item, "role") ?? "Unknown",
                    GetString(item, "ndiChannelName") ?? "",
                    GetString(item, "hdmiOutputResolution"),
                    GetBoolean(item, "isOnboarded"),
                    GetString(item, "health") ?? "Unknown",
                    includeCredentials ? ReadCredentials(item) : null));
            }
        }
        return new JobSnapshot(jobName, source, DateTimeOffset.Now, devices);
    }

    private async Task<JobSnapshot> MergeLocalCredentialsAsync(JobSnapshot snapshot, CancellationToken cancellationToken)
    {
        var path = ResolveStatePath();
        var document = await ReadDocumentAsync(path, cancellationToken)
            ?? await ReadDocumentAsync(path + ".bak", cancellationToken);
        if (document is null) return snapshot;

        using (document)
        {
            var local = ParseSnapshot(document.RootElement, path, includeCredentials: true);
            var byId = local.Devices.Where(device => !string.IsNullOrWhiteSpace(device.Id))
                .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Credentials, StringComparer.OrdinalIgnoreCase);
            var byIp = local.Devices.Where(device => !string.IsNullOrWhiteSpace(device.IpAddress))
                .GroupBy(device => device.IpAddress, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Credentials, StringComparer.OrdinalIgnoreCase);

            var merged = snapshot.Devices.Select(device =>
            {
                DeviceCredentials? credentials = null;
                if (!string.IsNullOrWhiteSpace(device.Id)) byId.TryGetValue(device.Id, out credentials);
                if (credentials is null && !string.IsNullOrWhiteSpace(device.IpAddress)) byIp.TryGetValue(device.IpAddress, out credentials);
                return device with { Credentials = credentials };
            }).ToArray();
            return snapshot with { Devices = merged };
        }
    }

    private static DeviceCredentials? ReadCredentials(JsonElement item)
    {
        if (!item.TryGetProperty("credentials", out var credentials) || credentials.ValueKind != JsonValueKind.Object) return null;
        var username = GetString(credentials, "username") ?? "";
        var password = GetString(credentials, "password") ?? "";
        return string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)
            ? null
            : new DeviceCredentials(username, password);
    }

    private static async Task<JobSnapshot?> TryReadApiAsync(string address, CancellationToken cancellationToken)
    {
        var root = address.TrimEnd('/');
        if (!Uri.TryCreate(root, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https")) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(850));
        using var client = new HttpClient { BaseAddress = new Uri(root + "/") };
        try
        {
            using var healthResponse = await client.GetAsync("api/health", timeout.Token);
            if (!healthResponse.IsSuccessStatusCode) return null;
            using var health = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync(timeout.Token));
            if (!health.RootElement.TryGetProperty("product", out var product) ||
                !string.Equals(product.GetString(), "NDI Job Configurator", StringComparison.OrdinalIgnoreCase)) return null;

            using var stateResponse = await client.GetAsync("api/state", timeout.Token);
            if (!stateResponse.IsSuccessStatusCode) return null;
            using var state = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync(timeout.Token));
            return ParseSnapshot(state.RootElement, baseUri.GetLeftPart(UriPartial.Authority));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    private static async Task<JobSnapshot?> DiscoverOnLanAsync(CancellationToken cancellationToken)
    {
        var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.GetAddressBytes())
            .DistinctBy(b => Convert.ToHexString(b))
            .ToArray();

        var hosts = localAddresses
            .SelectMany(bytes => Enumerable.Range(1, 254).Select(last => $"http://{bytes[0]}.{bytes[1]}.{bytes[2]}.{last}:{Port}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (hosts.Length == 0) return null;

        using var gate = new SemaphoreSlim(48);
        using var found = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        JobSnapshot? result = null;
        var tasks = hosts.Select(async host =>
        {
            await gate.WaitAsync(found.Token);
            try
            {
                if (result is not null) return;
                var candidate = await TryReadApiAsync(host, found.Token);
                if (candidate is null) return;
                if (Interlocked.CompareExchange(ref result, candidate, null) is null) found.Cancel();
            }
            catch (OperationCanceledException) { }
            finally { gate.Release(); }
        }).ToArray();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) when (result is not null) { }
        return result;
    }

    private static async Task<JsonDocument?> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (IOException) when (attempt < 2) { await Task.Delay(80, cancellationToken); }
            catch (JsonException) when (attempt < 2) { await Task.Delay(80, cancellationToken); }
        }
        return null;
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (!element.TryGetProperty(segment, out element)) return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
    }

    private static bool GetBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
