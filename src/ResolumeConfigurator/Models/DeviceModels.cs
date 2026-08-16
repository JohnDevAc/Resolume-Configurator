using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ResolumeConfigurator.Models;

public sealed record DeviceCredentials(string Username, string Password);

public sealed record JobDevice(
    string Id,
    string IpAddress,
    string Hostname,
    string Model,
    string Family,
    string Role,
    string NdiChannelName,
    string? HdmiOutputResolution,
    bool IsOnboarded,
    string Health,
    DeviceCredentials? Credentials = null);

public sealed record DecoderPresetResult(string DecoderName, string Family, int Slot, bool ReusedExistingSlot);

public sealed record JobSnapshot(string JobName, string Source, DateTimeOffset ReadAt, IReadOnlyList<JobDevice> Devices);

public abstract class ObservableRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public sealed class DecoderRow : ObservableRow
{
    private int _width;
    private int _height;

    public required int Order { get; init; }
    public required JobDevice Device { get; init; }
    public required string OutputName { get; init; }
    public string DeviceLabel => $"{Device.Hostname}  ·  {Device.Model}";
    public string IpAddress => Device.IpAddress;
    public string DetectedResolution => string.IsNullOrWhiteSpace(Device.HdmiOutputResolution) ? "Not reported" : Device.HdmiOutputResolution!;
    public bool UsedFallbackResolution { get; init; }
    public int Width { get => _width; set => Set(ref _width, value); }
    public int Height { get => _height; set => Set(ref _height, value); }
    public string ResolutionStatus => UsedFallbackResolution ? "Review" : "Detected";
}

public sealed class EncoderRow : ObservableRow
{
    private string _arenaSourceName = "";
    private string _arenaSourceIdString = "";
    private string _matchStatus = "Not checked";

    public required int Order { get; init; }
    public required JobDevice Device { get; init; }
    public string DeviceLabel => $"{Device.Hostname}  ·  {Device.Model}";
    public string IpAddress => Device.IpAddress;
    public string NdiChannelName => Device.NdiChannelName;
    public string ArenaSourceName { get => _arenaSourceName; set => Set(ref _arenaSourceName, value); }
    public string ArenaSourceIdString { get => _arenaSourceIdString; set => Set(ref _arenaSourceIdString, value); }
    public string ArenaSourceToken => string.IsNullOrWhiteSpace(ArenaSourceIdString) ? ArenaSourceName : ArenaSourceIdString;
    public string MatchStatus { get => _matchStatus; set => Set(ref _matchStatus, value); }
}

public sealed record ArenaSource(string IdString, string Name, string Category);
public sealed record ArenaProduct(string Name, int Major, int Minor, int Micro, int Revision)
{
    public override string ToString() => $"{Name} {Major}.{Minor}.{Micro}";
}

public sealed record ConfigurationPlan(
    string CompositionName,
    int CompositionWidth,
    int CompositionHeight,
    int FramesPerSecond,
    IReadOnlyList<DecoderRow> Decoders,
    IReadOnlyList<EncoderRow> Encoders,
    string PresetName,
    string CompositionDirectory,
    string PresetDirectory);

public sealed record ConfigurationResult(
    string CompositionFile,
    string PresetFile,
    string BackupFile,
    IReadOnlyList<string> Log,
    IReadOnlyList<DecoderPresetResult> DecoderPresets);
