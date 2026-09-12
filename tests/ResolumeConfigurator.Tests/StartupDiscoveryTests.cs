using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ResolumeConfigurator;
using ResolumeConfigurator.Models;
using ResolumeConfigurator.Services;
using static RegressionTests;

internal static class StartupDiscoveryTests
{
    private static void Assert(bool condition, string message) => RegressionTests.Assert(condition, message);
    private const string Local = "http://127.0.0.1:8091";
    private static JobSnapshot Snapshot(string address, string name = "Test job") => new(name, address, DateTimeOffset.Now, []);

    public static int Preview(string scenario)
    {
        var thread = new Thread(() =>
        {
            var app = new App(runStartup: false) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            var choices = scenario == "none" ? Array.Empty<ConfiguratorInstance>() : new[]
            {
                new ConfiguratorInstance("http://192.0.2.10:8091", "Preview — Main Stage", 4),
                new ConfiguratorInstance("http://192.0.2.20:8091", "Preview — Second Stage", 8)
            };
            var window = new ConfiguratorStartupWindow(async ct =>
            {
                await Task.Delay(350, ct);
                return new(null, choices);
            }, (address, _) => Task.FromResult<JobSnapshot?>(Snapshot(address)));
            var accepted = window.ShowDialog() == true;
            Console.WriteLine($"Startup preview: accepted={accepted}; selected={window.SelectedAddress ?? "none"}");
            app.Shutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return 0;
    }

    public static async Task LocalInstanceAsync()
    {
        var discovery = new NdiJobConfiguratorDiscovery((address, _) => Task.FromResult<JobSnapshot?>(Snapshot(address)),
            () => throw new InvalidOperationException("LAN must not be scanned on the hosting PC"), Local, TimeSpan.FromSeconds(1));
        var result = await discovery.DiscoverAsync();
        Assert(result.LocalInstance?.BaseAddress == Local && result.NetworkInstances.Count == 0, "local host should proceed without a network selection");
    }

    public static async Task AllNetworkInstancesAsync()
    {
        var probed = new System.Collections.Concurrent.ConcurrentBag<string>();
        var discovery = new NdiJobConfiguratorDiscovery(async (address, ct) =>
        {
            if (address == Local) return null;
            probed.Add(address);
            await Task.Delay(address.EndsWith('2') ? 30 : 1, ct);
            return Snapshot(address, "Same job name");
        }, () => ["http://192.0.2.1", "http://192.0.2.2/", "http://192.0.2.1/", "not a URL"], Local, TimeSpan.FromSeconds(1));
        var result = await discovery.DiscoverAsync();
        Assert(result.LocalInstance is null && result.NetworkInstances.Count == 2 && probed.Count == 2,
            "collect slow and fast responders, deduplicate addresses, and retain separate instances with the same job name");
    }

    public static async Task DiscoveryDeadlineAsync()
    {
        var empty = new NdiJobConfiguratorDiscovery((_, _) => Task.FromResult<JobSnapshot?>(null), () => ["http://192.0.2.1"], Local, TimeSpan.FromSeconds(1));
        var none = await empty.DiscoverAsync();
        Assert(none.LocalInstance is null && none.NetworkInstances.Count == 0, "no response must stay empty without consulting a state file");
        var discovery = new NdiJobConfiguratorDiscovery(async (address, ct) =>
        {
            if (address == Local) return null;
            if (address.EndsWith('1')) return Snapshot(address);
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }, () => ["http://192.0.2.1", "http://192.0.2.2"], Local, TimeSpan.FromMilliseconds(80));
        var partial = await discovery.DiscoverAsync();
        Assert(partial.ScanTimedOut && partial.NetworkInstances.Count == 1, "deadline preserves discovered choices");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        try { await discovery.DiscoverAsync(cancel.Token); throw new InvalidOperationException("caller cancellation was ignored"); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    public static async Task SelectedEndpointAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/ndi";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 4; i++)
            {
                using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var firstLine = await reader.ReadLineAsync(deadline.Token);
                Assert(firstLine == $"GET /ndi/api/{(i % 2 == 0 ? "health" : "state")} HTTP/1.1", "selected base path must survive refreshes");
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
                var body = Encoding.UTF8.GetBytes(i % 2 == 0 ? """{"product":"NDI Job Configurator"}""" : """{"integrationSchemaVersion":1,"lastJob":{"jobName":"Selected job"},"devices":[]}""");
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n"), deadline.Token);
                await stream.WriteAsync(body, deadline.Token);
            }
        });
        var jobReader = new NdiJobConfiguratorReader(address);
        for (var i = 0; i < 2; i++)
        {
            var snapshot = await jobReader.ReadAsync(deadline.Token);
            Assert(snapshot.Source == address && snapshot.JobName == "Selected job", "refresh must use the selected instance");
            var initial = await jobReader.ReadInitialAsync(snapshot, deadline.Token);
            Assert(initial.ReadAt == snapshot.ReadAt, "a fresh selected startup snapshot avoids duplicate HTTP requests");
        }
        await server;
        listener.Stop();
        using var offlineDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await jobReader.ReadInitialAsync(Snapshot(address) with { ReadAt = DateTimeOffset.Now.AddMinutes(-1) }, offlineDeadline.Token); throw new InvalidOperationException("stale startup snapshots must be reread"); }
        catch (HttpRequestException) { }
        try { await jobReader.ReadInitialAsync(Snapshot("http://192.0.2.99:8091"), offlineDeadline.Token); throw new InvalidOperationException("startup snapshot must belong to selected endpoint"); }
        catch (HttpRequestException) { }
        try { await jobReader.ReadAsync(offlineDeadline.Token); throw new InvalidOperationException("offline selection must not fall back to another instance or cache"); }
        catch (HttpRequestException) { }
    }

    public static void RunWindows()
    {
        var first = new ConfiguratorInstance("http://192.0.2.1:8091", "Stage A", 4);
        var second = new ConfiguratorInstance("http://192.0.2.2:8091", "Stage B", 8);
        foreach (var count in new[] { 0, 1, 2 })
        {
            var choices = new[] { first, second }.Take(count).ToArray();
            var window = new ConfiguratorStartupWindow(_ => Task.FromResult(new ConfiguratorDiscoveryResult(null, choices)),
                (address, _) => Task.FromResult<JobSnapshot?>(Snapshot(address)));
            var accepted = ExerciseDialog(window, () =>
            {
                if (count == 0)
                {
                    Assert(window.HeadingText.Text.Contains("No NDI Job Configurator") && window.ContinueButton.Visibility == Visibility.Collapsed,
                        "empty discovery must explain the exit and prevent proceeding");
                    window.ExitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else
                {
                    Assert(window.IsVisible && window.SelectedAddress is null, "even one remote instance must wait for user confirmation");
                    if (count == 2) Assert(!window.ContinueButton.IsEnabled, "multiple instances require an explicit selection");
                    window.InstancesGrid.SelectedIndex = count - 1;
                    Assert(window.ContinueButton.IsEnabled, "selection enables Continue");
                    window.ContinueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            });
            Assert(accepted == (count > 0) && window.SelectedAddress == (count > 0 ? choices[^1].BaseAddress : null), "dialog returns only the chosen endpoint or graceful cancellation");
        }
        var offline = new ConfiguratorStartupWindow(_ => Task.FromResult(new ConfiguratorDiscoveryResult(null, [first])), (_, _) => Task.FromResult<JobSnapshot?>(null));
        ExerciseDialog(offline, () =>
        {
            offline.ContinueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(offline.SelectedAddress is null && offline.StatusText.Text.Contains("no longer reachable") && offline.ContinueButton.IsEnabled,
                "an instance disappearing after discovery must not open the main application");
            offline.Close();
        });
        var canceled = false;
        var scanning = new ConfiguratorStartupWindow(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { canceled = true; throw; }
            return new(null, []);
        }, (_, _) => Task.FromResult<JobSnapshot?>(null));
        ExerciseDialog(scanning, () => scanning.Close());
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Assert(canceled, "closing startup cancels the network scan");
        var error = new ConfiguratorStartupWindow(_ => Task.FromException<ConfiguratorDiscoveryResult>(new IOException("network failure")), (_, _) => Task.FromResult<JobSnapshot?>(null));
        ExerciseDialog(error, () =>
        {
            Assert(error.HeadingText.Text == "Unable to search the network" && error.ContinueButton.Visibility == Visibility.Collapsed, "discovery errors should offer a graceful exit");
            error.Close();
        });
        var local = new ConfiguratorStartupWindow(_ => Task.FromResult(new ConfiguratorDiscoveryResult(first, [])), (_, _) => Task.FromResult<JobSnapshot?>(null));
        Assert(ExerciseDialog(local, () => throw new InvalidOperationException("local startup waited for selection")) && local.SelectedAddress == first.BaseAddress,
            "local startup continues automatically");
    }

    public static async Task ArenaReadinessAsync()
    {
        var probes = 0;
        var ready = await ArenaStartupService.WaitForWebserverAsync(_ => Task.FromResult(++probes == 2),
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert(ready && probes == 2, "stop waiting immediately once Arena is ready");
        var timedOut = await ArenaStartupService.WaitForWebserverAsync(async ct => { await Task.Delay(Timeout.Infinite, ct); return false; },
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert(!timedOut, "an unavailable webserver must reach a bounded end state");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        try
        {
            await ArenaStartupService.WaitForWebserverAsync(async ct => { await Task.Delay(Timeout.Infinite, ct); return false; },
                TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), cancel.Token);
            throw new InvalidOperationException("Arena startup ignored cancellation");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    public static void PreparedSourceMatching()
    {
        var sources = new[] { new ArenaSource("B", "Camera Z", "NDI Servers"), new ArenaSource("A", "Camera A", "NDI Servers") };
        var matcher = SourceMatcher.CreateMatcher(sources);
        var device = new JobDevice("test", "192.0.2.1", "Camera", "N6", "N6", "Encoder", "", null, true, "Online");
        Assert(matcher(device) is null, "a partial shared name must require a manual match");
        Assert(matcher(device with { Hostname = "Camera Z" })?.Name == "Camera Z", "exact match outranks partial matches in a reused source index");
        Assert(matcher(device with { Hostname = "Unknown" }) is null, "a reused source index must not leak a previous result");
    }

    private static bool ExerciseDialog(ConfiguratorStartupWindow window, Action inspect)
    {
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Assert(!Application.Current.Windows.OfType<MainWindow>().Any(), "startup dialogs must not open the main application before selection");
                inspect();
            }
            catch (Exception ex) { failure = ex; window.Close(); }
        };
        timer.Start();
        var accepted = window.ShowDialog() == true;
        timer.Stop();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return accepted;
    }
}
