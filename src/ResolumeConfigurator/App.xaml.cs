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
            _ = RunPostRestartWorkerAsync(e.Args[1]);
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private async Task RunPostRestartWorkerAsync(string expectedJobName)
    {
        try
        {
            var result = await new PostRestartWorker().RunAsync(expectedJobName, CancellationToken.None);
            await MainWindowFocusService.ReturnToMainWindowAsync(
                $"complete — {result.Count} decoder banks active", CancellationToken.None);
        }
        catch (Exception ex)
        {
            await MainWindowFocusService.ReturnToMainWindowAsync(
                $"decoder activation failed — {ex.Message}", CancellationToken.None);
        }
        finally { Shutdown(); }
    }
}
