using System.IO;
using System.Xml.Linq;
using ResolumeConfigurator.Models;
using ResolumeConfigurator.Services;

if (args.Length == 5 && args[0] == "--verify-job-contract")
{
    var snapshot = await new NdiJobConfiguratorReader(args[1]).ReadAsync(CancellationToken.None);
    JobRevisionGuard.Validate(new(args[2], args[3], args[4]), snapshot);
    if (snapshot.JobName != "Contract Fixture" || snapshot.DiscoveryServer != "192.0.2.5")
        throw new InvalidOperationException("The cross-application fixture did not retain its job/discovery contract.");
    Console.WriteLine("PASS Resolume consumed the real Job Configurator HTTP identity and revision");
    return 0;
}

if (args.Contains("--startup-preview", StringComparer.OrdinalIgnoreCase))
    return StartupDiscoveryTests.Preview(args.Last());

if (args.Contains("--inspect-fleet", StringComparer.OrdinalIgnoreCase))
    return await LiveIntegrationChecks.InspectFleetAsync(args);

if (args.Contains("--backup-arena", StringComparer.OrdinalIgnoreCase))
{
    var path = args.SkipWhile(argument => argument != "--backup-arena").Skip(1).First();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    using var api = new ResolumeApiClient();
    await api.SaveCompositionAsync(path, CancellationToken.None);
    Console.WriteLine($"Saved live composition to {path}");
    return 0;
}

if (args.Contains("--probe-arena", StringComparer.OrdinalIgnoreCase))
{
    var started = DateTime.UtcNow;
    using var api = new ResolumeApiClient(timeout: TimeSpan.FromSeconds(15));
    var name = await api.GetCompositionNameAsync(CancellationToken.None);
    var nameMilliseconds = (DateTime.UtcNow - started).TotalMilliseconds;
    var state = await api.GetCompositionStateAsync(CancellationToken.None);
    Console.WriteLine($"{name}|name={nameMilliseconds:0}ms|{state.Name}|{state.ColumnIds.Count}|{state.Groups.Count}|total={(DateTime.UtcNow - started).TotalMilliseconds:0}ms");
    return 0;
}

if (args.Contains("--probe-n6", StringComparer.OrdinalIgnoreCase))
{
    var requested = args.SkipWhile(argument => !argument.Equals("--probe-n6", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() ?? "KV-001";
    var snapshot = await new NdiJobConfiguratorReader().ReadAsync(CancellationToken.None);
    var device = snapshot.Devices.Single(device => device.IsOnboarded && device.Role.Equals("Decoder", StringComparison.OrdinalIgnoreCase)
        && device.Hostname.Contains(requested, StringComparison.OrdinalIgnoreCase));
    var row = new DecoderRow { Order = 1, Device = device, OutputName = device.Hostname, Width = 1920, Height = 1080 };
    var diagnostic = await new KiloviewDecoderPresetService().InspectN6Async(row, CancellationToken.None);
    Console.WriteLine($"decoder={diagnostic.DecoderName}");
    foreach (var preset in diagnostic.Presets) Console.WriteLine($"slot={preset.Id}|name={preset.StreamName}|url={preset.StreamUrl}");
    Console.WriteLine($"current={diagnostic.CurrentName}|url={diagnostic.CurrentUrl}");
    Console.WriteLine($"discovered={diagnostic.DiscoveredName}|url={diagnostic.DiscoveredUrl}");
    return 0;
}

if (args.Contains("--configure-n6", StringComparer.OrdinalIgnoreCase))
{
    var requested = args.SkipWhile(argument => !argument.Equals("--configure-n6", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() ?? "KV-001";
    var snapshot = await new NdiJobConfiguratorReader().ReadAsync(CancellationToken.None);
    var device = snapshot.Devices.Single(device => device.IsOnboarded && device.Role.Equals("Decoder", StringComparison.OrdinalIgnoreCase)
        && device.Hostname.Contains(requested, StringComparison.OrdinalIgnoreCase));
    var row = new DecoderRow { Order = 1, Device = device, OutputName = device.Hostname, Width = 1920, Height = 1080 };
    var result = (await new KiloviewDecoderPresetService().ConfigureAsync(new[] { row }, null, CancellationToken.None)).Single();
    Console.WriteLine($"decoder={result.DecoderName}|family={result.Family}|slot={result.Slot}|reused={result.ReusedExistingSlot}|activated=true");
    return 0;
}

var tests = new (string Name, Action Test)[]
{
    ("resolution parser", TestResolutionParser),
    ("source matcher", TestSourceMatcher),
    ("empty source identities", RegressionTests.EmptySourceIdentities),
    ("manual source correction", RegressionTests.ManualSourceCorrection),
    ("decoder preset name collisions", RegressionTests.DecoderPresetNameCollisions),
    ("invalid resolution boundaries", RegressionTests.InvalidResolutionBoundaries),
    ("streaming API timeouts", () => RegressionTests.StreamingApiTimeoutsAsync().GetAwaiter().GetResult()),
    ("local state backup recovery", () => RegressionTests.LocalStateRecoveryAsync().GetAwaiter().GetResult()),
    ("discovery cancellation", () => RegressionTests.DiscoveryCancellationAsync().GetAwaiter().GetResult()),
    ("job API response handling", () => RegressionTests.JobApiResponsesAsync().GetAwaiter().GetResult()),
    ("restart launch arguments", RegressionTests.RestartLaunchArguments),
    ("subnet discovery", RegressionTests.SubnetDiscovery),
    ("job credential selection", RegressionTests.JobCredentialSelection),
    ("job revision and local NDI readiness", RegressionTests.InteropReadiness),
    ("post-restart clip restoration", RegressionTests.RestartClipRestoration),
    ("stale Arena composition detection", RegressionTests.StaleArenaComposition),
    ("local configurator startup", () => StartupDiscoveryTests.LocalInstanceAsync().GetAwaiter().GetResult()),
    ("multiple network configurators", () => StartupDiscoveryTests.AllNetworkInstancesAsync().GetAwaiter().GetResult()),
    ("discovery deadline and cancellation", () => StartupDiscoveryTests.DiscoveryDeadlineAsync().GetAwaiter().GetResult()),
    ("selected configurator persistence", () => StartupDiscoveryTests.SelectedEndpointAsync().GetAwaiter().GetResult()),
    ("Arena readiness wait", () => StartupDiscoveryTests.ArenaReadinessAsync().GetAwaiter().GetResult()),
    ("prepared source matching", StartupDiscoveryTests.PreparedSourceMatching),
    ("configuration preflight", RegressionTests.ConfigurationPreflight),
    ("WPF validation and busy state", WpfRegressionTests.Run),
    ("Arena source URL", TestSourceUrl),
    ("advanced output preset", TestPreset),
    ("active advanced output XML", TestActiveAdvancedOutput),
    ("configuration options", TestConfigurationOptions),
    ("composition fps patch", TestFrameRatePatch),
    ("collapsed layer state", TestCollapsedLayerState),
    ("NDI composition sharing preference", TestNdiCompositionSharingPreference),
    ("Video Router input patch", TestVideoRouterInputPatch),
    ("N60 decoder preset slot selection", TestN60PresetSlotSelection),
    ("N6 decoder preset slot selection", TestN6PresetSlotSelection)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try { test.Test(); Console.WriteLine($"PASS  {test.Name}"); }
    catch (Exception ex) { failures.Add($"FAIL  {test.Name}: {ex.Message}"); }
}
foreach (var failure in failures) Console.Error.WriteLine(failure);
Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");
return failures.Count == 0 ? 0 : 1;

static void TestResolutionParser()
{
    Assert(ResolutionParser.TryParse("1920x1080p50", out var w, out var h) && w == 1920 && h == 1080, "progressive resolution");
    Assert(ResolutionParser.TryParse("3840 × 2160 @ 50", out w, out h) && w == 3840 && h == 2160, "formatted resolution");
    Assert(!ResolutionParser.TryParse("unknown", out _, out _), "invalid resolution rejected");
}

static void TestSourceMatcher()
{
    var device = Device("N6EncoderTest-KV-001", "N6EncoderTest-181", "Encoder");
    var match = SourceMatcher.BestMatch(device, new[]
    {
        new ArenaSource("OTHER", "Other", "NDI Servers"),
        new ArenaSource("N6ENCODERTEST-KV-001 (Decoding Channel)", "N6ENCODERTEST-KV-001 (Decoding Channel)", "NDI Servers")
    });
    Assert(match?.Name.StartsWith("N6ENCODERTEST-KV-001", StringComparison.Ordinal) == true, "hostname prefix match");
}

static void TestSourceUrl()
{
    var url = ResolumeApiClient.BuildVideoSourceUrl("N6ENCODERTEST-TT-001 (N6EncoderTest-182)");
    Assert(url == "source:///video/N6ENCODERTEST-TT-001%20%28N6EncoderTest-182%29", "RFC 3986 source encoding");
    Assert(!url.Contains('+'), "Arena-incompatible plus encoding rejected");
    Assert(ResolumeApiClient.BuildVideoSourceUrl("Video Router") == "source:///video/Video%20Router", "router display-name URL");
}

static void TestPreset()
{
    var decoder = new DecoderRow { Order = 1, Device = Device("Decoder A", "Decoder A", "Decoder"), OutputName = "Decoder A", Width = 1920, Height = 1080 };
    var encoder = new EncoderRow { Order = 1, Device = Device("Encoder A", "Encoder A", "Encoder"), ArenaSourceName = "Encoder A (Main)" };
    var plan = new ConfigurationPlan("Test", 3840, 2160, 50, 20, false, true, 5, new[] { decoder }, new[] { encoder }, "Test Outputs", ".", ".");
    var document = new AdvancedOutputPresetGenerator().Generate(plan, new ArenaProduct("Arena", 7, 27, 1, 15990));
    Assert(document.Root?.Name.LocalName == "XmlState", "loadable Arena preset wrapper");
    Assert(document.Root?.Element("ScreenSetup") is not null, "wrapped ScreenSetup");
    Assert(document.Descendants("OutputDeviceNDI").Count() == 2, "Show and decoder NDI device count");
    Assert(document.Descendants("OutputDeviceVirtual").Count() == 1, "LED Wall virtual device count");
    Assert(document.Descendants().Where(e => e.Name.LocalName.StartsWith("OutputDevice", StringComparison.Ordinal)).All(e => (string?)e.Attribute("idHash") != "0"), "real output identity hashes");
    Assert(document.Descendants("OutputDeviceNDI").All(e => ((string?)e.Attribute("deviceId"))?.StartsWith("NDIOutput ", StringComparison.Ordinal) == true), "Arena NDI device IDs");
    var routes = document.Descendants("Screen").ToDictionary(
        screen => screen.Attribute("name")!.Value,
        screen => screen.Descendants("ParamChoice").Single(choice => (string?)choice.Attribute("name") == "Input Source").Attribute("value")!.Value);
    Assert(routes["Decoder A"] == "1:1", "decoder group route");
    Assert(routes["LED Wall"] == "1:2", "LED group route");
    Assert(routes["Show"] == "1:3", "Show group route");
    var decoderScreen = document.Descendants("Screen").Single(s => (string?)s.Attribute("name") == "Decoder A");
    Assert(decoderScreen.Descendants("InputRect").Single().Elements("v").ElementAt(2).Attribute("x")?.Value == "3840", "composition input width");
    Assert(decoderScreen.Descendants("OutputRect").Single().Elements("v").ElementAt(2).Attribute("x")?.Value == "1920", "decoder output width");
}

static void TestConfigurationOptions()
{
    var decoder = new DecoderRow { Order = 1, Device = Device("Decoder A", "Decoder A", "Decoder"), OutputName = "Decoder A", Width = 1920, Height = 1080 };
    var encoders = new[]
    {
        new EncoderRow { Order = 1, Device = Device("Encoder A", "Encoder A", "Encoder"), ArenaSourceName = "Encoder A" },
        new EncoderRow { Order = 2, Device = Device("Encoder B", "Encoder B", "Encoder"), ArenaSourceName = "Encoder B" }
    };
    var automatic = new ConfigurationPlan("Test", 5120, 2880, 60, 20, false, true, 5, new[] { decoder }, encoders, "Outputs", ".", ".");
    ArenaConfigurationOrchestrator.ValidatePlan(automatic);
    Assert(ArenaConfigurationOrchestrator.DefaultColumnCount == 20, "default column count");
    Assert(ArenaConfigurationOrchestrator.MinimumColumnCount == 5 && ArenaConfigurationOrchestrator.MaximumColumnCount == 50, "column range");
    Assert(ArenaConfigurationOrchestrator.GetMinimumColumnCountForPlacement(5, encoders.Length, true) == 7, "dynamic source and router minimum");
    Assert(ArenaConfigurationOrchestrator.GetMinimumColumnCountForPlacement(5, encoders.Length, false) == 5, "manual placement restores minimum");
    Assert(ArenaConfigurationOrchestrator.GetSourceColumnIndices(automatic).SequenceEqual(new[] { 4, 5 }), "1-based column 5 source placement");
    Assert(ArenaConfigurationOrchestrator.GetRouterColumnIndex(automatic) == 6, "router follows placed sources in column 7");

    var manual = automatic with { AutoPlaceNdiSources = false };
    Assert(ArenaConfigurationOrchestrator.GetSourceColumnIndices(manual).Count == 0, "manual placement leaves source slots empty");
    Assert(ArenaConfigurationOrchestrator.GetRouterColumnIndex(manual) == 0, "manual placement keeps router in column 1");
}

static void TestActiveAdvancedOutput()
{
    var directory = Path.Combine(Path.GetTempPath(), $"resolume-output-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var decoder = new DecoderRow { Order = 1, Device = Device("Decoder A", "Decoder A", "Decoder"), OutputName = "Decoder A", Width = 1920, Height = 1080 };
        var encoder = new EncoderRow { Order = 1, Device = Device("Encoder A", "Encoder A", "Encoder"), ArenaSourceName = "Encoder A (Main)" };
        var plan = new ConfigurationPlan("Test", 3840, 2160, 50, 20, false, true, 5, new[] { decoder }, new[] { encoder }, "Test Outputs", directory, directory);
        var preset = new AdvancedOutputPresetGenerator().Generate(plan, new ArenaProduct("Arena", 7, 27, 1, 15990));
        var presetFile = Path.Combine(directory, "preset.xml");
        preset.Save(presetFile);
        var preferenceFile = Path.Combine(directory, "AdvancedOutput.xml");
        var expected = new[] { "Show", "LED Wall", "Decoder A" };
        new AdvancedOutputActivator().ActivateAsync(presetFile, preferenceFile, expected, CancellationToken.None).GetAwaiter().GetResult();

        var active = XDocument.Load(preferenceFile);
        Assert(active.Root?.Name.LocalName == "ScreenSetup", "active XML uses Arena preference root");
        Assert(active.Root?.Element("versionInfo") is not null, "active XML carries Arena version metadata");
        Assert(AdvancedOutputActivator.PreferenceMatches(preferenceFile, expected), "active XML screen verification");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static void TestFrameRatePatch()
{
    var path = Path.Combine(Path.GetTempPath(), $"resolume-configurator-{Guid.NewGuid():N}.avc");
    try
    {
        File.WriteAllText(path, "<Composition><VideoTrack><Params><ParamRange name=\"Width\" value=\"3840\"/></Params></VideoTrack></Composition>");
        ArenaConfigurationOrchestrator.SetCompositionFrameRate(path, 50);
        var document = XDocument.Load(path);
        Assert(document.Descendants("ParamRange").Single(e => (string?)e.Attribute("name") == "FrameRate").Attribute("value")?.Value == "50", "50 fps inserted");
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static void TestCollapsedLayerState()
{
    var path = Path.Combine(Path.GetTempPath(), $"resolume-layers-{Guid.NewGuid():N}.avc");
    try
    {
        File.WriteAllText(path, "<Composition><Layer><Params><Param name=\"Name\" value=\"Holding\"/></Params><LayerView foldedControl=\"0\"/></Layer><Layer><Params><Param name=\"Name\" value=\"Secondary\"/></Params><LayerView foldedControl=\"0\"/></Layer><Layer><Params><Param name=\"Name\" value=\"Primary\"/></Params><LayerView foldedControl=\"1\"/></Layer></Composition>");
        ArenaConfigurationOrchestrator.SetCollapsedLayerStates(path);
        var layers = XDocument.Load(path).Root!.Elements("Layer").ToDictionary(
            layer => layer.Descendants("Param").Single().Attribute("value")!.Value,
            layer => layer.Element("LayerView")!.Attribute("foldedControl")!.Value);
        Assert(layers["Holding"] == "1" && layers["Secondary"] == "1", "Holding and Secondary collapsed");
        Assert(layers["Primary"] == "0", "Primary expanded");
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static void TestNdiCompositionSharingPreference()
{
    var directory = Path.Combine(Path.GetTempPath(), $"resolume-simple-output-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "SimpleOutput.xml");
    try
    {
        File.WriteAllText(path, "<SimpleSetup advancedModeEnabled=\"1\"><Outputs/></SimpleSetup>");
        var service = new SimpleOutputConfigurationService();
        service.ApplyNdiCompositionSharingAsync(path, true, 7680, 4320, CancellationToken.None).GetAwaiter().GetResult();
        Assert(SimpleOutputConfigurationService.PreferenceMatches(path, true), "NDI preference enabled");
        var device = XDocument.Load(path).Descendants("OutputDeviceNDI").Single();
        Assert((string?)device.Attribute("name") == "Composition", "composition output name");
        Assert((string?)device.Attribute("width") == "7680" && (string?)device.Attribute("height") == "4320", "composition output dimensions");
        service.ApplyNdiCompositionSharingAsync(path, false, 7680, 4320, CancellationToken.None).GetAwaiter().GetResult();
        Assert(SimpleOutputConfigurationService.PreferenceMatches(path, false), "NDI preference disabled");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static void TestVideoRouterInputPatch()
{
    var path = Path.Combine(Path.GetTempPath(), $"resolume-router-{Guid.NewGuid():N}.avc");
    try
    {
        File.WriteAllText(path, "<Composition><VideoSource type=\"CompositionRouterVideoSource\"/><VideoSource type=\"CompositionRouterVideoSource\"><Params name=\"Settings\"><ParamChoice name=\"Input\" value=\"0:0\"/></Params></VideoSource></Composition>");
        ArenaConfigurationOrchestrator.SetVideoRouterInputs(path, 5, 2);
        var document = XDocument.Load(path);
        var inputs = document.Descendants("VideoSource").Select(e => e.Descendants("ParamChoice").Single()).ToArray();
        Assert(inputs.All(e => (string?)e.Attribute("value") == "1:5"), "all routers target final Show index");
        Assert(inputs.All(e => (string?)e.Attribute("default") == "0:0" && (string?)e.Attribute("storeChoices") == "0"), "Arena router schema retained");
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static void TestN60PresetSlotSelection()
{
    var presets = new[]
    {
        new N60PresetSummary(1, "Camera", "Encoder (Camera)", "", false),
        new N60PresetSummary(2, "", "", "", true),
        new N60PresetSummary(3, "N6EncoderTest-KV-002", "JOHN-PC (N6EncoderTest-KV-002)", "", false),
        new N60PresetSummary(10, "", "", "#000000", false)
    };
    Assert(KiloviewDecoderPresetService.SelectN60Slot(presets, "N6EncoderTest-KV-002", out var reused) == 3 && reused, "reuse matching N60 slot");
    Assert(KiloviewDecoderPresetService.SelectN60Slot(presets, "New Arena Output", out reused) == 2 && !reused, "choose first empty N60 slot");
}

static void TestN6PresetSlotSelection()
{
    var presets = new[]
    {
        new N6PresetSummary(1, "Camera"),
        new N6PresetSummary(3, "JOHN-PC (N6EncoderTest-KV-001)")
    };
    Assert(KiloviewDecoderPresetService.SelectN6Slot(presets, "N6EncoderTest-KV-001", out var reused) == 3 && reused, "reuse matching N6 slot");
    Assert(KiloviewDecoderPresetService.SelectN6Slot(presets, "New Arena Output", out reused) == 0 && !reused, "request firmware-managed next empty N6 slot");
    Assert(KiloviewDecoderPresetService.IsSameN6Source("sender-1", "ndi://192.168.0.16:5965", "sender-2", "ndi://192.168.0.16:5965"), "recognize unchanged N6 source URL");
    Assert(!KiloviewDecoderPresetService.IsSameN6Source("sender-1", "ndi://192.168.0.16:5965", "sender-2", "ndi://192.168.0.20:5965"), "recognize changed N6 source URL");
    Assert(KiloviewDecoderPresetService.IsActiveN6Source(
        "JOHN-PC (Arena - N6EncoderTest-KV-001)", "", "192.168.0.16:5965",
        "N6EncoderTest-KV-001", "192.168.0.16:5964"), "accept N6 listener-port normalization for the named Arena output");
    Assert(!KiloviewDecoderPresetService.IsActiveN6Source(
        "JOHN-PC (Arena - N6EncoderTest-KV-001)", "", "192.168.0.20:5965",
        "N6EncoderTest-KV-001", "192.168.0.16:5964"), "reject a matching name from a different NDI host");
}

static JobDevice Device(string hostname, string channel, string role) => new("id", "192.168.0.1", hostname, "N60", "N60", role, channel, "1920x1080p50", true, "Online", new("test-user", "test-password"));
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
