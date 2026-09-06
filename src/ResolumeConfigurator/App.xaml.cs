using System.Windows;
using ResolumeConfigurator.Services;
using ResolumeConfigurator.Models;

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

        if (e.Args.Length >= 2 && e.Args[0].Equals("--post-restart", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int? parentProcessId = e.Args.Length >= 3 && int.TryParse(e.Args[2], out var processId) && processId > 0 ? processId : null;
            var restoration = e.Args.Length >= 6 && int.TryParse(e.Args[4], out var startColumn) && int.TryParse(e.Args[5], out var sourceCount)
                ? new PostRestartComposition(e.Args[3], startColumn, sourceCount) : null;
            var configuratorUrl = e.Args.Length >= 7 ? e.Args[6] : null;
            var expectedJob = e.Args.Length >= 10 ? new JobIdentity(e.Args[7], e.Args[8], e.Args[9]) : null;
            _ = RunPostRestartWorkerAsync(e.Args[1], parentProcessId, restoration, configuratorUrl, expectedJob);
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

    private async Task RunPostRestartWorkerAsync(string expectedJobName, int? parentProcessId, PostRestartComposition? restoration, string? configuratorUrl, JobIdentity? expectedJob)
    {
        try
        {
            var result = await new PostRestartWorker().RunAsync(expectedJobName, CancellationToken.None, restoration, configuratorUrl, expectedJob);
            await MainWindowFocusService.ReturnToMainWindowAsync(
                $"complete — {result.Count} decoder banks active", CancellationToken.None, parentProcessId);
        }
        catch (Exception ex)
        {
            await MainWindowFocusService.ReturnToMainWindowAsync(
                $"decoder activation failed — {ex.Message}", CancellationToken.None, parentProcessId);
        }
        finally { Shutdown(); }
    }
}
