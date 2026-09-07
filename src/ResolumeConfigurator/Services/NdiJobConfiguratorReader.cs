using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public sealed class NdiJobConfiguratorReader
{
    private const int Port = 8091;
    private static readonly HttpClient ApiClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string? _selectedAddress;

    public NdiJobConfiguratorReader(string? selectedAddress = null)
    {
        _selectedAddress = selectedAddress is null ? null : NormalizeBaseAddress(selectedAddress)
            ?? throw new ArgumentException("The selected configurator address must be an HTTP or HTTPS base URL.", nameof(selectedAddress));
    }

    public string ResolveStatePath()
    {
        var directory = ArenaPaths.ResolveOverride("NDI_JOB_CONFIGURATOR_DATA_DIR")
            ?? ArenaPaths.ResolveOverride("KILOVIEW_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NDI Job Configurator");
        return Path.Combine(directory, "state.json");
    }

    public async Task<JobSnapshot> ReadAsync(CancellationToken cancellationToken = default, bool includeLocalCredentials = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var address = _selectedAddress;
        if (address is null)
        {
            var discovery = await new NdiJobConfiguratorDiscovery().DiscoverAsync(cancellationToken).ConfigureAwait(false);
            if (discovery.LocalInstance is not null) address = discovery.LocalInstance.BaseAddress;
            else if (discovery.NetworkInstances.Count == 1) address = discovery.NetworkInstances[0].BaseAddress;
            else if (discovery.NetworkInstances.Count > 1)
                throw new InvalidOperationException("Multiple NDI Job Configurators were found. Open the application and select one before continuing.");
            else throw new HttpRequestException($"No NDI Job Configurator was found on TCP {Port}.");
        }
        // Never rediscover or use cached job data when the selected server goes offline.
        var snapshot = await TryReadApiCoreAsync(address, TimeSpan.FromSeconds(5), cancellationToken, reportTimeout: true).ConfigureAwait(false)
            ?? throw new HttpRequestException($"The selected NDI Job Configurator at {address} is unavailable. Check its connection and try again.");
        return includeLocalCredentials ? await MergeLocalCredentialsAsync(snapshot, cancellationToken).ConfigureAwait(false) : snapshot;
    }

    internal Task<JobSnapshot> ReadInitialAsync(JobSnapshot? discoveredSnapshot, CancellationToken ct = default) =>
        discoveredSnapshot is not null && discoveredSnapshot.Source.Equals(_selectedAddress, StringComparison.OrdinalIgnoreCase)
        && DateTimeOffset.Now - discoveredSnapshot.ReadAt is var age && age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(5)
            ? MergeLocalCredentialsAsync(discoveredSnapshot, ct) : ReadAsync(ct);

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
        var serverId = GetString(root, "serverId");
        var jobId = GetString(root, "jobId");
        var revision = GetString(root, "jobRevision");
        var identity = Guid.TryParse(serverId, out var serverGuid) && serverGuid != Guid.Empty
            && !string.IsNullOrWhiteSpace(jobId) && !string.IsNullOrWhiteSpace(revision)
                ? new JobIdentity(serverId!, jobId, revision) : null;
        return new JobSnapshot(jobName, source, DateTimeOffset.Now, devices, identity, GetString(root, "lastJob", "ndiDiscoveryServerIp"));
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
        var localDevices = local is not null && snapshot.Identity is not null && local.Identity == snapshot.Identity
            && local.JobName.Equals(snapshot.JobName, StringComparison.Ordinal)
            ? local.Devices : [];
        var byId = localDevices.Where(device => !string.IsNullOrWhiteSpace(device.Id))
            .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Credentials, StringComparer.OrdinalIgnoreCase);

        var merged = snapshot.Devices.Select(device =>
        {
            DeviceCredentials? credentials = null;
            if (!string.IsNullOrWhiteSpace(device.Id)) byId.TryGetValue(device.Id, out credentials);
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

    internal static Task<JobSnapshot?> TryReadApiAsync(string address, CancellationToken cancellationToken) =>
        TryReadApiCoreAsync(address, TimeSpan.FromMilliseconds(850), cancellationToken);

    internal static async Task<JobSnapshot?> TryReadApiCoreAsync(string address, TimeSpan requestTimeout, CancellationToken cancellationToken, bool reportTimeout = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = NormalizeBaseAddress(address);
        if (root is null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
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
            return ParseSnapshot(state.RootElement, root);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && reportTimeout)
        { throw new HttpRequestException($"The selected Job Configurator at {root} did not complete its health/state response within {requestTimeout.TotalSeconds:0.#} seconds.", ex); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && (ex is HttpRequestException or OperationCanceledException or JsonException)) { return null; }
    }

    internal static string? NormalizeBaseAddress(string address) =>
        Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.AbsoluteUri.TrimEnd('/') : null;

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
