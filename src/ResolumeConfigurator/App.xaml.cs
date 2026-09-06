using System.Windows;
using ResolumeConfigurator.Services;

namespace ResolumeConfigurator;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 2 && e.Args[0].Equals("--post-restart", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int? parentProcessId = e.Args.Length >= 3 && int.TryParse(e.Args[2], out var processId) && processId > 0 ? processId : null;
            var restoration = e.Args.Length >= 6 && int.TryParse(e.Args[4], out var startColumn) && int.TryParse(e.Args[5], out var sourceCount)
                ? new PostRestartComposition(e.Args[3], startColumn, sourceCount) : null;
            _ = RunPostRestartWorkerAsync(e.Args[1], parentProcessId, restoration);
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private async Task RunPostRestartWorkerAsync(string expectedJobName, int? parentProcessId, PostRestartComposition? restoration)
    {
        try
        {
            var result = await new PostRestartWorker().RunAsync(expectedJobName, CancellationToken.None, restoration);
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
