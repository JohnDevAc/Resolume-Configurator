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
    private static readonly HttpClient ApiClient = new() { Timeout = Timeout.InfiniteTimeSpan };

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
        cancellationToken.ThrowIfCancellationRequested();
        var configuredUrl = Environment.GetEnvironmentVariable("NDI_JOB_CONFIGURATOR_URL");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredUrl)) candidates.Add(configuredUrl);
        candidates.Add($"http://127.0.0.1:{Port}");

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = await TryReadApiAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null) return await MergeLocalCredentialsAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        var discovered = await DiscoverOnLanAsync(cancellationToken).ConfigureAwait(false);
        if (discovered is not null) return await MergeLocalCredentialsAsync(discovered, cancellationToken).ConfigureAwait(false);

        var path = ResolveStatePath();
        return await ReadLocalSnapshotAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"NDI Job Configurator was not found on TCP {Port} or in its local state folder.", path);
    }

    internal static async Task<JobSnapshot?> ReadLocalSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        using var document = await ReadDocumentAsync(path, cancellationToken).ConfigureAwait(false)
            ?? await ReadDocumentAsync(path + ".bak", cancellationToken).ConfigureAwait(false);
        return document is null ? null : ParseSnapshot(document.RootElement, path, includeCredentials: true);
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
        var local = await ReadLocalSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
        return MergeCredentials(snapshot, local);
    }

    internal static JobSnapshot MergeCredentials(JobSnapshot snapshot, JobSnapshot? local)
    {
        // NDI Job Configurator provisions onboarded Kiloviews with its job
        // credentials. A state file from a previous job must not override them.
        var localDevices = local is not null && local.JobName.Equals(snapshot.JobName, StringComparison.Ordinal)
            ? local.Devices : [];
        var byId = localDevices.Where(device => !string.IsNullOrWhiteSpace(device.Id))
            .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Credentials, StringComparer.OrdinalIgnoreCase);
        var byIp = localDevices.Where(device => !string.IsNullOrWhiteSpace(device.IpAddress))
            .GroupBy(device => device.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Credentials, StringComparer.OrdinalIgnoreCase);

        var merged = snapshot.Devices.Select(device =>
        {
            DeviceCredentials? credentials = null;
            if (!string.IsNullOrWhiteSpace(device.Id)) byId.TryGetValue(device.Id, out credentials);
            if (credentials is null && !string.IsNullOrWhiteSpace(device.IpAddress)) byIp.TryGetValue(device.IpAddress, out credentials);
            if (credentials is null && device.IsOnboarded && device.Role.Equals("Decoder", StringComparison.OrdinalIgnoreCase)
                && (device.Family.Equals("N6", StringComparison.OrdinalIgnoreCase) || device.Family.Equals("N60", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(snapshot.JobName))
                credentials = new DeviceCredentials("admin", snapshot.JobName);
            return device with { Credentials = credentials };
        }).ToArray();
        return snapshot with { Devices = merged };
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

    internal static async Task<JobSnapshot?> TryReadApiAsync(string address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = address.TrimEnd('/');
        if (!Uri.TryCreate(root, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https")) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(850));
        var apiBaseUri = new Uri(root + "/");
        try
        {
            using var healthResponse = await ApiClient.GetAsync(new Uri(apiBaseUri, "api/health"), timeout.Token).ConfigureAwait(false);
            if (!healthResponse.IsSuccessStatusCode) return null;
            using var health = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            if (health.RootElement.ValueKind != JsonValueKind.Object ||
                !health.RootElement.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.String ||
                !string.Equals(product.GetString(), "NDI Job Configurator", StringComparison.OrdinalIgnoreCase)) return null;

            using var stateResponse = await ApiClient.GetAsync(new Uri(apiBaseUri, "api/state"), timeout.Token).ConfigureAwait(false);
            if (!stateResponse.IsSuccessStatusCode) return null;
            using var state = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            if (!IsSnapshot(state.RootElement)) return null;
            return ParseSnapshot(state.RootElement, baseUri.GetLeftPart(UriPartial.Authority));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && (ex is HttpRequestException or OperationCanceledException or JsonException)) { return null; }
    }

    private static async Task<JobSnapshot?> DiscoverOnLanAsync(CancellationToken cancellationToken)
    {
        var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => (a.Address, a.PrefixLength))
            .Distinct()
            .OrderByDescending(a => a.PrefixLength)
            .ToArray();

        // Probe nearby addresses first, then the rest of each actual subnet.
        // Keep enumeration lazy: a /16 must not allocate 65,534 tasks or URLs.
        var hosts = localAddresses
            .SelectMany(a => EnumerateSubnetHosts(a.Address, Math.Max(24, a.PrefixLength)))
            .Concat(localAddresses.SelectMany(a => EnumerateSubnetHosts(a.Address, a.PrefixLength)))
            .Distinct()
            .Select(address => $"http://{address}:{Port}");
        if (localAddresses.Length == 0) return null;

        using var found = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Discovery is best effort; unreachable wide/VPN subnets must not hold
        // up the local state fallback indefinitely. An explicit URL is tried first.
        found.CancelAfter(TimeSpan.FromSeconds(15));
        JobSnapshot? result = null;
        try
        {
            await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = 48, CancellationToken = found.Token }, async (host, token) =>
            {
                var candidate = await TryReadApiAsync(host, token).ConfigureAwait(false);
                if (candidate is null) return;
                if (Interlocked.CompareExchange(ref result, candidate, null) is null) found.Cancel();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal static IEnumerable<IPAddress> EnumerateSubnetHosts(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        var bytes = address.GetAddressBytes();
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var mask = prefixLength == 0 ? 0U : uint.MaxValue << (32 - prefixLength);
        var network = value & mask;
        var broadcast = network | ~mask;
        var first = prefixLength >= 31 ? (ulong)network : (ulong)network + 1;
        var last = prefixLength >= 31 ? (ulong)broadcast : (ulong)broadcast - 1;
        for (var host = first; host <= last; host++)
            yield return new IPAddress(new[] { (byte)(host >> 24), (byte)(host >> 16), (byte)(host >> 8), (byte)host });
    }

    private static async Task<JsonDocument?> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (IsSnapshot(document.RootElement)) return document;
                document.Dispose();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            if (attempt < 2) await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static bool IsSnapshot(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("devices", out var devices)
        && devices.ValueKind == JsonValueKind.Array && devices.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object);

    private static string? GetString(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element)) return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
    }

    private static bool GetBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
