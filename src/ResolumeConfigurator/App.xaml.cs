using System.Windows;
using ResolumeConfigurator.Services;

namespace ResolumeConfigurator;

public partial class App : Application
{
    private readonly bool _runStartup;

    public App() : this(true) { }

    // UI regression tests need the real resources without dispatching startup I/O.
    internal App(bool runStartup) => _runStartup = runStartup;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!_runStartup) return;

        if (e.Args.Length == 2 && e.Args[0] == "--post-restart-request")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunTrackedWorkerAsync(e.Args[1]);
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--post-restart", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Legacy requests lack a pinned parent lifetime and shared ownership.
            Shutdown(1);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var startup = new ConfiguratorStartupWindow();
        if (startup.ShowDialog() != true || startup.SelectedAddress is null)
        {
            Shutdown();
            return;
        }
        MainWindow = new MainWindow(startup.SelectedAddress, startup.SelectedSnapshot);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow.Show();
    }

    private async Task RunTrackedWorkerAsync(string requestFile)
    {
        var exitCode = 1;
        RestartRequest? request = null;
        try
        {
            request = System.Text.Json.JsonSerializer.Deserialize<RestartRequest>(await File.ReadAllTextAsync(requestFile))
                ?? throw new InvalidDataException("The restart request is empty.");
            using var deadline = new CancellationTokenSource(WorkerCompletion.WorkerTimeout);
            await using var parent = new ParentLifetime(request.ParentProcessId, request.ParentStartUtcTicks, deadline.Token);
            using var ownership = ConfigurationOwnership.AcquireWorker();
            parent.Validate();
            var decoders = await new PostRestartWorker().RunAsync(request.JobName, parent.Token, request.Composition,
                request.ConfiguratorUrl, request.ExpectedJob, request.Agent, parent.Validate);
            await AtomicFile.WriteJsonAsync(Path.Combine(Path.GetDirectoryName(requestFile)!, "worker-result.json"),
                new WorkerOutcome(request.OperationId, true, null, decoders), CancellationToken.None);
            exitCode = 0;
        }
        catch (Exception ex)
        {
            if (request is not null)
            {
                try
                {
                    await AtomicFile.WriteJsonAsync(Path.Combine(Path.GetDirectoryName(requestFile)!, "worker-result.json"),
                        new WorkerOutcome(request.OperationId, false, ex.Message, []), CancellationToken.None);
                }
                catch { /* The parent detects exit without a readable result. */ }
            }
        }
        finally { Shutdown(exitCode); }
    }
}
