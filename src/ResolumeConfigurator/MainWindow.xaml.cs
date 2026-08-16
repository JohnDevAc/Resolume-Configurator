using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ResolumeConfigurator.Models;
using ResolumeConfigurator.Services;

namespace ResolumeConfigurator;

public partial class MainWindow : Window
{
    private readonly NdiJobConfiguratorReader _jobReader = new();
    private JobSnapshot? _snapshot;
    private ArenaProduct? _product;
    private bool _busy;
    private bool _decoderHelperPending;
    private bool _updatingSetupConstraints;
    private string _compositionName = "NDI Job";
    private const int DecoderHelperStatusMessage = 0x8001;

    public ObservableCollection<DecoderRow> Decoders { get; } = [];
    public ObservableCollection<EncoderRow> Encoders { get; } = [];
    public IReadOnlyList<WorkspaceResolutionOption> WorkspaceResolutions { get; } =
    [
        new("1080p", 1920, 1080),
        new("4K", 3840, 2160),
        new("5K", 5120, 2880),
        new("8K", 7680, 4320)
    ];
    public IReadOnlyList<int> FrameRates { get; } = [60, 50];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        WorkspaceResolutionComboBox.SelectedItem = WorkspaceResolutions.Single(option => option.Name == "4K");
        FrameRateComboBox.SelectedItem = 50;
        WorkspaceResolutionComboBox.SelectionChanged += (_, _) => UpdatePlan();
        FrameRateComboBox.SelectionChanged += (_, _) => UpdatePlan();
        ColumnCountSlider.ValueChanged += (_, _) => UpdatePlan();
        SourceStartColumnSlider.ValueChanged += (_, _) => UpdatePlan();
        NdiCompositionSharingToggle.Checked += (_, _) => UpdatePlan();
        NdiCompositionSharingToggle.Unchecked += (_, _) => UpdatePlan();
        AutoPlaceSourcesToggle.Checked += (_, _) => UpdatePlan();
        AutoPlaceSourcesToggle.Unchecked += (_, _) => UpdatePlan();
        SourceInitialized += (_, _) => ((HwndSource)PresentationSource.FromVisual(this)).AddHook(WindowMessageHook);
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        Topmost = true;
        Activate();
        ConfigurationProgress.Value = 0;
        ConfigurationProgress.IsIndeterminate = true;
        try
        {
            ArenaStatusText.Text = "Arena: checking process…";
            var startup = await new ArenaStartupService().EnsureRunningAsync(CancellationToken.None);
            if (startup.Launched)
            {
                ArenaStatusText.Text = "Arena: startup wait complete";
                AppendLog("Arena was not running, so it was launched. Waited 15 seconds before discovery.");
            }
        }
        catch (Exception ex)
        {
            ArenaStatusText.Text = "Arena: could not start";
            AppendLog("ERROR: " + ex.Message);
        }

        await RefreshAsync();
        ConfigurationProgress.IsIndeterminate = false;
        ConfigurationProgress.Value = 0;
        Topmost = false;
        Activate();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true);
        LogBox.Clear();
        Decoders.Clear();
        Encoders.Clear();
        EncoderMatchWarningText.Visibility = Visibility.Collapsed;
        _product = null;
        try
        {
            _snapshot = await _jobReader.ReadAsync();
            _compositionName = _snapshot.JobName;
            JobStatusText.Text = _snapshot.JobName;
            JobStatusText.ToolTip = $"{_snapshot.Devices.Count} device{Plural(_snapshot.Devices.Count)} in the current NDI Configurator job";

            var decoderDevices = _snapshot.Devices.Where(d => d.IsOnboarded && d.Role.Equals("Decoder", StringComparison.OrdinalIgnoreCase)).ToArray();
            var encoderDevices = _snapshot.Devices.Where(d => d.IsOnboarded && d.Role.Equals("Encoder", StringComparison.OrdinalIgnoreCase)).ToArray();
            for (var i = 0; i < decoderDevices.Length; i++)
            {
                var detected = ResolutionParser.TryParse(decoderDevices[i].HdmiOutputResolution, out var width, out var height);
                Decoders.Add(new DecoderRow
                {
                    Order = i + 1, Device = decoderDevices[i], OutputName = decoderDevices[i].Hostname,
                    Width = detected ? width : 1920, Height = detected ? height : 1080, UsedFallbackResolution = !detected
                });
            }
            for (var i = 0; i < encoderDevices.Length; i++)
                Encoders.Add(new EncoderRow { Order = i + 1, Device = encoderDevices[i] });

            using var api = new ResolumeApiClient();
            try
            {
                _product = await api.GetProductAsync();
                var sources = await api.GetNdiSourcesAsync();
                ArenaStatusText.Text = $"Arena: {_product.Major}.{_product.Minor}.{_product.Micro} online";
                foreach (var encoder in Encoders)
                {
                    var match = SourceMatcher.BestMatch(encoder.Device, sources);
                    encoder.ArenaSourceName = match?.Name ?? "";
                    encoder.ArenaSourceIdString = match?.IdString ?? "";
                    encoder.MatchStatus = match is null ? "Missing" : "Matched";
                    encoder.PropertyChanged += Row_OnPropertyChanged;
                }
                AppendLog($"Arena reported {sources.Count} live NDI source(s).");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                ArenaStatusText.Text = "Arena: webserver unavailable";
                AppendLog("Start Arena and enable Preferences > Webserver on port 8080.");
            }

            foreach (var decoder in Decoders) decoder.PropertyChanged += Row_OnPropertyChanged;
            DecoderCountText.Text = Decoders.Count.ToString();
            EncoderCountText.Text = Encoders.Count.ToString();
            AppendLog($"Read {Decoders.Count} decoder(s) and {Encoders.Count} encoder(s) in NDI Job Configurator order.");
            UpdatePlan();
        }
        catch (Exception ex)
        {
            JobStatusText.Text = "JOB UNAVAILABLE";
            JobStatusText.ToolTip = null;
            ArenaStatusText.Text = "Arena: not checked";
            AppendLog(ex.Message);
            UpdatePlan();
        }
        finally { SetBusy(false); }
    }

    private void Row_OnPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdatePlan();

    private void UpdatePlan()
    {
        if (_updatingSetupConstraints || WorkspaceResolutionComboBox is null || FrameRateComboBox is null || ColumnCountSlider is null) return;
        UpdateSetupConstraints();
        var resolution = WorkspaceResolutionComboBox.SelectedItem as WorkspaceResolutionOption ?? WorkspaceResolutions[1];
        var framesPerSecond = FrameRateComboBox.SelectedItem is int selectedFrameRate ? selectedFrameRate : 50;
        var totalColumnCount = (int)ColumnCountSlider.Value;
        var sourceStartColumn = (int)SourceStartColumnSlider.Value;
        var autoPlaceSources = AutoPlaceSourcesToggle.IsChecked == true;
        var sharingStatus = NdiCompositionSharingToggle.IsChecked == true ? "NDI SHARING ON" : "NDI SHARING OFF";
        ConfigurationSummaryText.Text = $"{resolution.Name.ToUpperInvariant()}  ·  {framesPerSecond} FPS  ·  {totalColumnCount} COLUMNS  ·  {sharingStatus}";

        var unmatchedSources = Encoders.Count(e => string.IsNullOrWhiteSpace(e.ArenaSourceToken));
        EncoderMatchWarningText.Visibility = unmatchedSources > 0 ? Visibility.Visible : Visibility.Collapsed;
        var missingSources = autoPlaceSources ? unmatchedSources : 0;
        var reviewResolutions = Decoders.Count(d => d.UsedFallbackResolution);

        var errors = new List<string>();
        if (_product is null) errors.Add("Arena webserver is not connected");
        if (Decoders.Count == 0) errors.Add("no onboarded decoders were found");
        if (autoPlaceSources && Encoders.Count == 0) errors.Add("no onboarded encoders were found for automatic placement");
        if (autoPlaceSources && Encoders.Count + 1 > ArenaConfigurationOrchestrator.MaximumColumnCount)
            errors.Add($"{Encoders.Count} encoder sources plus the Video Router exceed the {ArenaConfigurationOrchestrator.MaximumColumnCount}-column maximum");
        if (Decoders.Any(decoder => decoder.Device.Credentials is null)) errors.Add("saved decoder credentials are missing from NDI Job Configurator");
        if (autoPlaceSources && sourceStartColumn + Encoders.Count > totalColumnCount)
            errors.Add($"sources starting at column {sourceStartColumn} leave no column for the Video Router");
        if (missingSources > 0) errors.Add($"{missingSources} encoder source{Plural(missingSources)} need matching");
        if (string.IsNullOrWhiteSpace(_compositionName)) errors.Add("composition name is empty");
        if (Decoders.Any(d => d.Width is < 320 or > 32768 || d.Height is < 240 or > 32768)) errors.Add("a decoder resolution is invalid");

        ConfigureButton.IsEnabled = !_busy && errors.Count == 0;
        ValidationText.Text = errors.Count == 0
            ? reviewResolutions > 0 ? $"Ready — review {reviewResolutions} fallback decoder resolution{Plural(reviewResolutions)}." : "Ready to configure Arena."
            : "Not ready — " + string.Join("; ", errors) + ".";
    }

    private void UpdateSetupConstraints()
    {
        _updatingSetupConstraints = true;
        try
        {
            var autoPlaceSources = AutoPlaceSourcesToggle.IsChecked == true;
            var requestedStartColumn = Math.Max(1, (int)SourceStartColumnSlider.Value);
            var dynamicMinimum = ArenaConfigurationOrchestrator.GetMinimumColumnCountForPlacement(
                requestedStartColumn,
                Encoders.Count,
                autoPlaceSources);
            ColumnCountSlider.Minimum = dynamicMinimum;

            var availableStartMaximum = autoPlaceSources
                ? Math.Max(1, (int)ColumnCountSlider.Value - Encoders.Count)
                : (int)ColumnCountSlider.Value;
            SourceStartColumnSlider.Maximum = availableStartMaximum;
            ColumnCountHelpText.Text = autoPlaceSources
                ? $"Minimum {dynamicMinimum}: starting offset + {Encoders.Count} source{Plural(Encoders.Count)} + Video Router. Maximum 50."
                : "Choose between 5 and 50 columns.";
        }
        finally { _updatingSetupConstraints = false; }
    }

    private ConfigurationPlan BuildPlan()
    {
        var paths = ArenaPaths.Resolve();
        var resolution = WorkspaceResolutionComboBox.SelectedItem as WorkspaceResolutionOption ?? WorkspaceResolutions[1];
        var framesPerSecond = FrameRateComboBox.SelectedItem is int selectedFrameRate ? selectedFrameRate : 50;
        return new ConfigurationPlan(
            _compositionName.Trim(), resolution.Width, resolution.Height, framesPerSecond,
            (int)ColumnCountSlider.Value,
            NdiCompositionSharingToggle.IsChecked == true,
            AutoPlaceSourcesToggle.IsChecked == true,
            (int)SourceStartColumnSlider.Value,
            Decoders.ToArray(), Encoders.ToArray(),
            _compositionName.Trim() + " - NDI Outputs", paths.Compositions, paths.AdvancedOutputPresets);
    }

    private async void ConfigureButton_OnClick(object sender, RoutedEventArgs e)
    {
        var plan = BuildPlan();
        Title = "Resolume Arena Configurator — configuring";
        Topmost = true;
        Activate();
        ConfigurationProgress.Value = 0;
        ConfigurationProgress.IsIndeterminate = true;
        SetBusy(true);
        LogBox.Clear();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(AppendLog);
            var result = await new ArenaConfigurationOrchestrator().ConfigureAsync(plan, progress, cancellation.Token);
            _decoderHelperPending = true;
            AppendLog($"Arena restart initiated. Decoder activation will complete in this window. Preset: {result.PresetFile}");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
            Title = "Resolume Arena Configurator — configuration stopped";
            ConfigurationProgress.IsIndeterminate = false;
            ConfigurationProgress.Value = 0;
        }
        finally
        {
            SetBusy(false);
            if (!_decoderHelperPending) Topmost = false;
            UpdatePlan();
        }
    }

    private async void RefreshDetectionButton_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void SetBusy(bool value)
    {
        _busy = value;
        Cursor = value ? System.Windows.Input.Cursors.Wait : null;
        RefreshDetectionButton.IsEnabled = !value;
        if (value) ConfigureButton.IsEnabled = false;
        else UpdatePlan();
    }
    private void AppendLog(string message) { LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}"); LogBox.ScrollToEnd(); }
    private IntPtr WindowMessageHook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != DecoderHelperStatusMessage) return IntPtr.Zero;
        handled = true;
        _decoderHelperPending = false;
        SetBusy(false);
        ConfigurationProgress.IsIndeterminate = false;
        Topmost = false;
        Activate();
        if (wParam == new IntPtr(1))
        {
            ConfigurationProgress.Value = 100;
            ValidationText.Text = "Configuration complete — all decoder banks are active.";
            AppendLog("Decoder activation complete.");
        }
        else
        {
            ConfigurationProgress.Value = 0;
            ValidationText.Text = "Decoder activation failed — see the window title for details.";
            AppendLog("ERROR: Decoder activation failed after Arena restart.");
        }
        return IntPtr.Zero;
    }
    private static string Plural(int count) => count == 1 ? "" : "s";
}
