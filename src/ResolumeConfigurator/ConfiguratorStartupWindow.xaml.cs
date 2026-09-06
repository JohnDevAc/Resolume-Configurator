using System.Windows;
using System.Windows.Controls;
using ResolumeConfigurator.Models;
using ResolumeConfigurator.Services;

namespace ResolumeConfigurator;

public partial class ConfiguratorStartupWindow : Window
{
    private readonly Func<CancellationToken, Task<ConfiguratorDiscoveryResult>> _discover;
    private readonly Func<string, CancellationToken, Task<JobSnapshot?>> _probe;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closed;
    private bool _connecting;
    public string? SelectedAddress { get; private set; }
    public JobSnapshot? SelectedSnapshot { get; private set; }

    public ConfiguratorStartupWindow() : this(new NdiJobConfiguratorDiscovery().DiscoverAsync, NdiJobConfiguratorReader.TryReadApiAsync) { }

    internal ConfiguratorStartupWindow(Func<CancellationToken, Task<ConfiguratorDiscoveryResult>> discover,
        Func<string, CancellationToken, Task<JobSnapshot?>> probe)
    {
        InitializeComponent();
        _discover = discover;
        _probe = probe;
        Loaded += async (_, _) => await DiscoverAsync();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private async Task DiscoverAsync()
    {
        var ct = _lifetime.Token;
        try
        {
            var result = await _discover(ct);
            if (_closed) return;
            if (result.LocalInstance is not null)
            {
                SelectedAddress = result.LocalInstance.BaseAddress;
                SelectedSnapshot = result.LocalInstance.Snapshot;
                DialogResult = true;
                return;
            }
            DiscoveryProgress.Visibility = Visibility.Collapsed;
            if (result.NetworkInstances.Count == 0)
            {
                ShowExitMessage("No NDI Job Configurator found",
                    "No running NDI Job Configurator was found on this PC or the network. Start it on a PC connected to the same network, then reopen this application. Click Close application to exit.");
                return;
            }
            HeadingText.Text = "Select NDI Job Configurator";
            StatusText.Text = $"Found {result.NetworkInstances.Count} network instance{(result.NetworkInstances.Count == 1 ? "" : "s")}. Choose the job to use, then continue."
                + (result.ScanTimedOut ? " The search time limit was reached; other network segments may not have been fully scanned." : "");
            InstancesGrid.ItemsSource = result.NetworkInstances;
            InstancesGrid.Visibility = ContinueButton.Visibility = Visibility.Visible;
            if (result.NetworkInstances.Count == 1) InstancesGrid.SelectedIndex = 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closed) ShowExitMessage("Unable to search the network",
                "The network search could not be completed. Check your network connection and reopen the application. Click Close application to exit.");
        }
    }

    private void ShowExitMessage(string heading, string message)
    {
        HeadingText.Text = heading;
        StatusText.Text = message;
        DiscoveryProgress.Visibility = InstancesGrid.Visibility = ContinueButton.Visibility = Visibility.Collapsed;
        ExitButton.Content = "Close application";
        ExitButton.IsDefault = true;
        ExitButton.Focus();
    }

    private void InstancesGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContinueButton is not null) ContinueButton.IsEnabled = !_connecting && InstancesGrid.SelectedItem is ConfiguratorInstance;
    }

    private async void ContinueButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_connecting || _closed || InstancesGrid.SelectedItem is not ConfiguratorInstance selected) return;
        _connecting = true;
        InstancesGrid.IsEnabled = ContinueButton.IsEnabled = false;
        DiscoveryProgress.Visibility = Visibility.Visible;
        StatusText.Text = $"Connecting to {selected.BaseAddress}…";
        var ct = _lifetime.Token;
        try
        {
            var snapshot = await _probe(selected.BaseAddress, ct);
            if (_closed) return;
            if (snapshot is null)
            {
                StatusText.Text = "This configurator is no longer reachable. Select another instance, try again, or close the application.";
                return;
            }
            SelectedAddress = selected.BaseAddress;
            SelectedSnapshot = snapshot;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closed) StatusText.Text = "Could not connect to this configurator. Select another instance, try again, or close the application.";
        }
        finally
        {
            _connecting = false;
            if (!_closed)
            {
                DiscoveryProgress.Visibility = Visibility.Collapsed;
                InstancesGrid.IsEnabled = true;
                ContinueButton.IsEnabled = InstancesGrid.SelectedItem is ConfiguratorInstance;
            }
        }
    }

    private void ExitButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
