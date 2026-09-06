using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ResolumeConfigurator;
using ResolumeConfigurator.Models;
using static RegressionTests;

internal static class WpfRegressionTests
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new App(runStartup: false);
            try
            {
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                // Construct without showing: Loaded never fires, so no Arena process
                // or job discovery is started by this UI regression test.
                var window = new MainWindow();
                var plan = ValidPlan();
                window.Decoders.Add(plan.Decoders[0]);
                window.Encoders.Add(plan.Encoders[0]);
                SetField(window, "_product", new ArenaProduct("Arena", 7, 27, 1, 1));
                Invoke(window, "SetBusy", false);
                RegressionTests.Assert(window.ConfigureButton.IsEnabled, "valid inputs enable Configure");

                var binding = (BindingExpression)BindingOperations.SetBinding(window.DecoderGrid, FrameworkElement.TagProperty,
                    new Binding(nameof(DecoderRow.Width)) { Source = plan.Decoders[0], NotifyOnValidationError = true });
                Validation.MarkInvalid(binding, new ValidationError(new ExceptionValidationRule(), binding, "Not a number", null));
                RegressionTests.Assert(!window.ConfigureButton.IsEnabled, "invalid numeric text must block configuration even when the model retains its previous width");
                Validation.ClearInvalid(binding);
                RegressionTests.Assert(window.ConfigureButton.IsEnabled, "correcting the invalid text enables Configure again");

                SetField(window, "_decoderHelperPending", true);
                Invoke(window, "SetBusy", false);
                RegressionTests.Assert(!window.ConfigureButton.IsEnabled && !window.RefreshDetectionButton.IsEnabled && !window.ConfigurationInputs.IsEnabled,
                    "configuration and editing remain disabled until the restart worker completes");
                Invoke(window, "WindowMessageHook", IntPtr.Zero, 0x8001, new IntPtr(1), IntPtr.Zero, false);
                RegressionTests.Assert(window.ConfigureButton.IsEnabled && window.RefreshDetectionButton.IsEnabled && window.ConfigurationInputs.IsEnabled,
                    "worker completion restores the controls");
                RegressionTests.Assert(window.ConfigurationProgress.Value == 100, "worker success completes the progress bar");
                window.Close();
                StartupDiscoveryTests.RunWindows();
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("WPF regression test timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void SetField(MainWindow window, string name, object value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static void Invoke(MainWindow window, string name, params object[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
}
