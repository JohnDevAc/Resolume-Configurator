using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public sealed record ConfiguratorInstance(string BaseAddress, string JobName, int DeviceCount, JobSnapshot? Snapshot = null)
{
    public string DeviceSummary => $"{DeviceCount} device{(DeviceCount == 1 ? "" : "s")}";
}

public sealed record ConfiguratorDiscoveryResult(ConfiguratorInstance? LocalInstance,
    IReadOnlyList<ConfiguratorInstance> NetworkInstances, bool ScanTimedOut = false);

public sealed class NdiJobConfiguratorDiscovery
{
    private readonly Func<string, CancellationToken, Task<JobSnapshot?>> _probe;
    private readonly Func<IEnumerable<string>> _networkCandidates;
    private readonly string _localAddress;
    private readonly TimeSpan _scanBudget;

    public NdiJobConfiguratorDiscovery() : this(NdiJobConfiguratorReader.TryReadApiAsync, EnumerateNetworkCandidates,
        "http://127.0.0.1:8091", TimeSpan.FromSeconds(15)) { }

    internal NdiJobConfiguratorDiscovery(Func<string, CancellationToken, Task<JobSnapshot?>> probe,
        Func<IEnumerable<string>> networkCandidates, string localAddress, TimeSpan scanBudget)
    {
        _probe = probe;
        _networkCandidates = networkCandidates;
        _localAddress = localAddress;
        _scanBudget = scanBudget;
    }

    public async Task<ConfiguratorDiscoveryResult> DiscoverAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var local = await _probe(_localAddress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (local is not null) return new(ToInstance(local), []);

        var instances = new ConcurrentDictionary<string, ConfiguratorInstance>(StringComparer.OrdinalIgnoreCase);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_scanBudget);
        var timedOut = false;
        try
        {
            var candidates = _networkCandidates().Select(NdiJobConfiguratorReader.NormalizeBaseAddress)
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 48, CancellationToken = deadline.Token },
                async (address, token) =>
                {
                    var snapshot = await _probe(address, token).ConfigureAwait(false);
                    if (snapshot is not null) instances.TryAdd(snapshot.Source, ToInstance(snapshot));
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { timedOut = true; }
        ct.ThrowIfCancellationRequested();
        return new(null, instances.Values.OrderBy(instance => instance.JobName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.BaseAddress, StringComparer.OrdinalIgnoreCase).ToArray(), timedOut);
    }

    private static ConfiguratorInstance ToInstance(JobSnapshot snapshot) => new(snapshot.Source, snapshot.JobName, snapshot.Devices.Count, snapshot);

    private static IEnumerable<string> EnumerateNetworkCandidates()
    {
        var configured = Environment.GetEnvironmentVariable("NDI_JOB_CONFIGURATOR_URL");
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
            .Select(address => (address.Address, address.PrefixLength)).Distinct()
            .OrderByDescending(address => address.PrefixLength).ToArray();
        var hosts = interfaces.SelectMany(address => NdiJobConfiguratorReader.EnumerateSubnetHosts(address.Address, Math.Max(24, address.PrefixLength)))
            .Concat(interfaces.SelectMany(address => NdiJobConfiguratorReader.EnumerateSubnetHosts(address.Address, address.PrefixLength))).Distinct();
        foreach (var host in hosts) yield return $"http://{host}:8091";
    }
}
