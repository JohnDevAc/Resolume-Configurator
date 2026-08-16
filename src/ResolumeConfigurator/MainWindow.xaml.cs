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
    private string _compositionName = "NDI Job";
    private const int DecoderHelperStatusMessage = 0x8001;

    public ObservableCollection<DecoderRow> Decoders { get; } = [];
    public ObservableCollection<EncoderRow> Encoders { get; } = [];
    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
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
        _product = null;
        try
        {
            _snapshot = await _jobReader.ReadAsync();
            _compositionName = _snapshot.JobName;
            JobStatusText.Text = $"NDI job: {_snapshot.Devices.Count} devices";

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
            JobStatusText.Text = "NDI job: unavailable";
            ArenaStatusText.Text = "Arena: not checked";
            AppendLog(ex.Message);
            UpdatePlan();
        }
        finally { SetBusy(false); }
    }

    private void Row_OnPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdatePlan();

    private void UpdatePlan()
    {
        var missingSources = Encoders.Count(e => string.IsNullOrWhiteSpace(e.ArenaSourceToken));
        var reviewResolutions = Decoders.Count(d => d.UsedFallbackResolution);
        PlanSummaryText.Text =
            $"• Show at top, LED Wall second, then {Decoders.Count} decoder group{Plural(Decoders.Count)}\n" +
            $"• Every group has Primary / Secondary / Holding layers\n" +
            $"• Exactly {ArenaConfigurationOrchestrator.TotalColumnCount} columns; {Encoders.Count} encoder source{Plural(Encoders.Count)} copied to all three layers in columns 1–{Math.Max(Encoders.Count, 1)}\n" +
            $"• The same encoder order is also loaded into all three Show layers\n" +
            $"• Decoder Primary layers get an enabled Video Router → Show in column {Encoders.Count + 1}\n" +
            $"• NDI clips are resized to Fit and their live thumbnails are refreshed\n" +
            $"• {Decoders.Count + 1} NDI screen{Plural(Decoders.Count + 1)} (Show + decoders), plus one LED Wall virtual output\n" +
            $"• Each decoder reuses its matching app-managed preset slot, or takes the next empty slot, then activates it\n" +
            $"• Composition: 3840 × 2160 at 50 fps; Arena restarts to activate Advanced Output";

        var errors = new List<string>();
        if (_product is null) errors.Add("Arena webserver is not connected");
        if (Decoders.Count == 0) errors.Add("no onboarded decoders were found");
        if (Encoders.Count == 0) errors.Add("no onboarded encoders were found");
        if (Decoders.Any(decoder => decoder.Device.Credentials is null)) errors.Add("saved decoder credentials are missing from NDI Job Configurator");
        if (Encoders.Count + 1 > ArenaConfigurationOrchestrator.TotalColumnCount) errors.Add($"more than {ArenaConfigurationOrchestrator.TotalColumnCount - 1} encoders cannot fit with the Video Router");
        if (missingSources > 0) errors.Add($"{missingSources} encoder source{Plural(missingSources)} need matching");
        if (string.IsNullOrWhiteSpace(_compositionName)) errors.Add("composition name is empty");
        if (Decoders.Any(d => d.Width is < 320 or > 32768 || d.Height is < 240 or > 32768)) errors.Add("a decoder resolution is invalid");

        ConfigureButton.IsEnabled = !_busy && errors.Count == 0;
        ValidationText.Text = errors.Count == 0
            ? reviewResolutions > 0 ? $"Ready — review {reviewResolutions} fallback decoder resolution{Plural(reviewResolutions)}." : "Ready to configure Arena."
            : "Not ready — " + string.Join("; ", errors) + ".";
    }

    private ConfigurationPlan BuildPlan()
    {
        var paths = ArenaPaths.Resolve();
        return new ConfigurationPlan(
            _compositionName.Trim(), 3840, 2160, 50,
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

    private void SetBusy(bool value) { _busy = value; ConfigureButton.IsEnabled = !value; Cursor = value ? System.Windows.Input.Cursors.Wait : null; }
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
