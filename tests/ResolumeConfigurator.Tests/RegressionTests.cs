using System.Net;
using System.IO;
using System.Net.Sockets;
using System.Text;
using ResolumeConfigurator.Models;
using ResolumeConfigurator.Services;

internal static class RegressionTests
{
    public static void InteropReadiness()
    {
        var local = new { schemaVersion = 1, endpointId = Guid.NewGuid().ToString(), adapterId = Guid.NewGuid().ToString(), address = "192.0.2.20", prefixLength = 24 };
        var localJson = System.Text.Json.JsonSerializer.SerializeToElement(local);
        var nic = new LocalNdiReadinessService.LocalAdapterAddress(local.adapterId, local.address, 24, true);
        var selected = LocalNdiReadinessService.ValidateLocalState(localJson, [nic]);
        LocalNdiReadinessService.ValidateStatusIdentity(localJson, selected);
        foreach (var wrong in new[] { nic with { AdapterId = Guid.NewGuid().ToString() }, nic with { Ready = false }, nic with { Prefix = 25 } })
            ExpectInvalid(() => LocalNdiReadinessService.ValidateLocalState(localJson, [wrong]), "an address on the wrong/down/changed adapter must fail");
        foreach (var property in new[] { "schemaVersion", "endpointId", "adapterId", "address", "prefixLength" })
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(localJson.GetRawText())!;
            node[property] = null;
            var invalid = System.Text.Json.JsonSerializer.SerializeToElement(node);
            ExpectInvalid(() => LocalNdiReadinessService.ValidateLocalState(invalid, [nic]), "incomplete local identity must fail");
            ExpectInvalid(() => LocalNdiReadinessService.ValidateStatusIdentity(invalid, selected), "mismatched responding identity must fail");
        }
        var identity = new JobIdentity(Guid.NewGuid().ToString(), "job-1", "revision-1");
        var job = new JobSnapshot("Test", "Remote API", DateTimeOffset.Now, [], identity, "192.0.2.5", 1);
        JobRevisionGuard.Validate(identity, job);
        ExpectInvalid(() => JobRevisionGuard.Validate(null, job), "legacy server must be updated before mutation");
        foreach (var other in new[] { identity with { ServerId = Guid.NewGuid().ToString() }, identity with { JobId = "job-2" }, identity with { Revision = "revision-2" } })
            ExpectInvalid(() => JobRevisionGuard.Validate(other, job), "server, same-name job replacement and configuration changes invalidate approval");
        using var status = System.Text.Json.JsonDocument.Parse("""
            {"endpointId":"test-endpoint","ndiConfiguration":{"preferredInterfaceConfigured":true,"sendGroups":["Test"],"receiveGroups":["Test"],"discoveryServer":"192.0.2.5"}}
            """);
        LocalNdiReadinessService.Validate(status.RootElement, "test-endpoint", job);
        ExpectInvalid(() => LocalNdiReadinessService.Validate(status.RootElement, "other-endpoint", job), "wrong local identity must fail");
        ExpectInvalid(() => LocalNdiReadinessService.Validate(status.RootElement, "test-endpoint", job with { JobName = "Other" }), "wrong groups must fail");
        ExpectInvalid(() => LocalNdiReadinessService.Validate(status.RootElement, "test-endpoint", job with { DiscoveryServer = "192.0.2.6" }), "wrong discovery must fail");
    }

    public static void StaleArenaComposition()
    {
        var saved = System.Xml.Linq.XDocument.Parse("""
            <Composition numColumns="2" currentDeckIndex="0">
              <Group uniqueId="10"/><Layer uniqueId="20" layerGroup="0"/>
              <Deck><Clip uniqueId="30" layerIndex="0" columnIndex="0"/><Clip uniqueId="31" layerIndex="0" columnIndex="1"/></Deck>
            </Composition>
            """);
        var layer = new ArenaLayerState(20, "Primary", [30, 31]);
        var live = new ArenaCompositionState("Job", [layer], [new(10, "Show", [layer])], [40, 41]);
        Assert(ArenaCompositionSynchronizationService.MatchesSavedStructure(saved, live), "matching saved/live graph should not restart Arena");
        Assert(!ArenaCompositionSynchronizationService.MatchesSavedStructure(saved, live with { Layers = [layer with { Id = 21 }] }), "stale layers must be detected even when counts match");
        Assert(!ArenaCompositionSynchronizationService.MatchesSavedStructure(saved, live with { Layers = [layer with { ClipIds = [32, 33] }] }), "stale clip identities must be detected");
        Assert(!ArenaCompositionSynchronizationService.MatchesSavedStructure(saved, live with { Groups = [new(10, "Show", [])] }), "stale group membership must be detected");
        Assert(!ArenaCompositionSynchronizationService.MatchesSavedStructure(saved, live with { ColumnIds = [40] }), "column differences must be detected");
    }

    public static void EmptySourceIdentities()
    {
        var device = Device("---", "...");
        Assert(SourceMatcher.BestMatch(device, [new("Camera A", "Camera A", "NDI Servers")]) is null,
            "punctuation-only device names must not match an arbitrary live feed");
    }

    public static void ManualSourceCorrection()
    {
        var row = new EncoderRow { Order = 1, Device = Device("Camera A", "Main"), ArenaSourceName = "Camera A (Main)", ArenaSourceIdString = "original-token" };
        row.ArenaSourceName = "Camera B (Main)";
        Assert(row.ArenaSourceToken == "Camera B (Main)", "editing a matched source must replace its old source token");
        row.ArenaSourceName = "";
        Assert(string.IsNullOrWhiteSpace(row.ArenaSourceToken), "clearing a source must require a new match");
    }

    public static void DecoderPresetNameCollisions()
    {
        Assert(KiloviewDecoderPresetService.SelectN6Slot([new(1, "PC (Arena - KV-0010)")], "KV-001", out var reused) == 0 && !reused,
            "KV-001 must not overwrite the KV-0010 preset");
        Assert(KiloviewDecoderPresetService.SelectN60Slot([new(1, "KV-0010", "PC (KV-0010)", "", false), new(2, "", "", "", true)], "KV-001", out reused) == 2 && !reused,
            "N60 must preserve a preset whose name merely contains the requested output");
        Assert(KiloviewDecoderPresetService.SelectN6Slot([new(1, "PC (Arena - KV-001)", "ndi://192.0.2.10:5961")], "KV-001", out reused, "192.0.2.10") == 1 && reused,
            "Arena's decorated output name must still match");
    }

    public static void InvalidResolutionBoundaries()
    {
        Assert(!ResolutionParser.TryParse("11920x108000", out _, out _), "six-digit height must not be truncated into a valid resolution");
        Assert(!ResolutionParser.TryParse("119200x1080", out _, out _), "six-digit width must not be truncated into a valid resolution");
    }

    public static async Task StreamingApiTimeoutsAsync()
    {
        foreach (var productRequest in new[] { true, false })
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/api/v1/";
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var server = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = connection.GetStream();
                var buffer = new byte[4096];
                await stream.ReadAsync(buffer, stop.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 200\r\n\r\n{"), stop.Token);
                await Task.Delay(Timeout.Infinite, stop.Token);
            });
            try
            {
                using var api = new ResolumeApiClient(address, TimeSpan.FromMilliseconds(150));
                var request = productRequest ? (Task)api.GetProductAsync(stop.Token) : api.GetCompositionNameAsync(stop.Token);
                try
                {
                    await request.WaitAsync(TimeSpan.FromSeconds(1));
                    throw new InvalidOperationException("an incomplete response cannot succeed");
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
                catch (TimeoutException)
                {
                    stop.Cancel();
                    try { await request; } catch (OperationCanceledException) { }
                    throw new InvalidOperationException($"{(productRequest ? "product" : "composition name")} body ignored the configured HTTP timeout");
                }
            }
            finally
            {
                stop.Cancel();
                try { await server; } catch (OperationCanceledException) { }
            }
        }
    }

    public static async Task LocalStateRecoveryAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"resolume-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        try
        {
            File.WriteAllText(path + ".bak", """{"lastJob":{"jobName":"Backup job"},"devices":[{"id":"test","credentials":{"username":"test-user","password":"test-password"}}]}""");
            foreach (var invalidState in new[] { "{", "null", "[]", "{\"devices\":{}}", "{\"devices\":[null]}" })
            {
                File.WriteAllText(path, invalidState);
                var snapshot = await NdiJobConfiguratorReader.ReadLocalSnapshotAsync(path, CancellationToken.None);
                Assert(snapshot?.JobName == "Backup job", "an invalid primary state must fall back to its backup");
                Assert(snapshot!.Devices.Single().Credentials?.Username == "test-user", "backup recovery must preserve local credentials");
            }
            File.WriteAllText(path, """{"lastJob":{"jobName":"Current job"},"devices":[]}""");
            Assert((await NdiJobConfiguratorReader.ReadLocalSnapshotAsync(path, CancellationToken.None))?.JobName == "Current job", "valid primary state takes precedence");
            File.WriteAllText(path, "{");
            File.WriteAllText(path + ".bak", "{");
            Assert(await NdiJobConfiguratorReader.ReadLocalSnapshotAsync(path, CancellationToken.None) is null, "unusable files allow API discovery to continue without credentials");
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task DiscoveryCancellationAsync()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await new NdiJobConfiguratorReader().ReadAsync(canceled.Token);
            throw new InvalidOperationException("canceled discovery must not return a job");
        }
        catch (OperationCanceledException) { }
        try
        {
            await NdiJobConfiguratorReader.ReadLocalSnapshotAsync("missing-state.json", canceled.Token);
            throw new InvalidOperationException("canceled file recovery must not continue");
        }
        catch (OperationCanceledException) { }
    }

    public static void RestartLaunchArguments()
    {
        var jobName = "Show A/B: Main Stage";
        var compositionFile = Path.Combine(Path.GetTempPath(), ArenaPaths.SafeFileName(jobName, "job") + ".avc");
        var arena = ArenaRestartService.CreateArenaStartInfo(@"C:\Program Files\Resolume Arena\Arena.exe", compositionFile);
        Assert(arena.ArgumentList.SequenceEqual(new[] { compositionFile }), "Arena must reopen the saved job composition, including spaces in its path");
        var worker = ArenaRestartService.CreateWorkerStartInfo(@"C:\Programs\Configurator.exe", jobName, 1234, compositionFile, 5, 2);
        Assert(worker.ArgumentList.SequenceEqual(new[] { "--post-restart", jobName, "1234", compositionFile, "5", "2" }), "the worker must receive the original job, originating app, and source placement");
        var selectedUrl = "http://192.0.2.2:8091/ndi";
        var selectedWorker = ArenaRestartService.CreateWorkerStartInfo(@"C:\Programs\Configurator.exe", jobName, 1234, compositionFile, 5, 2, selectedUrl);
        Assert(selectedWorker.ArgumentList.Last() == selectedUrl && selectedWorker.ArgumentList.Count == 7,
            "the fresh helper must receive the user's selected server, including its base path");
        var identity = new JobIdentity(Guid.NewGuid().ToString(), "job-1", "revision-1");
        var guarded = ArenaRestartService.CreateWorkerStartInfo(@"C:\Programs\Configurator.exe", jobName, 1234, compositionFile, 5, 2, selectedUrl, identity);
        Assert(guarded.ArgumentList.Skip(7).SequenceEqual(new[] { identity.ServerId, identity.JobId, identity.Revision }), "the restart helper lost its approved job identity");
    }

    public static void SubnetDiscovery()
    {
        var narrow = NdiJobConfiguratorReader.EnumerateSubnetHosts(IPAddress.Parse("192.168.0.170"), 25).Select(ip => ip.ToString()).ToArray();
        Assert(narrow.Length == 126 && narrow.First() == "192.168.0.129" && narrow.Last() == "192.168.0.254", "a /25 must stay inside its real subnet");
        var wide = NdiJobConfiguratorReader.EnumerateSubnetHosts(IPAddress.Parse("192.168.0.15"), 23).Select(ip => ip.ToString()).ToArray();
        Assert(wide.Length == 510 && wide.Contains("192.168.1.200") && wide.Contains("192.168.0.255"), "a /23 must include hosts outside the local /24");
        Assert(NdiJobConfiguratorReader.EnumerateSubnetHosts(IPAddress.Parse("10.0.0.1"), 31).Count() == 2, "point-to-point subnet includes both addresses");
        Assert(NdiJobConfiguratorReader.EnumerateSubnetHosts(IPAddress.Parse("10.0.0.1"), 32).Single().ToString() == "10.0.0.1", "host subnet is bounded");
    }

    public static void JobCredentialSelection()
    {
        var device = Device("Decoder", "Output") with { Role = "Decoder", Credentials = null };
        var current = new JobSnapshot("Current job", "API", DateTimeOffset.Now, [device], new(Guid.NewGuid().ToString(), "job-id", "revision"));
        var saved = current with { Devices = [device with { Credentials = new("saved-user", "saved-password") }] };
        var credentials = NdiJobConfiguratorReader.MergeCredentials(current, saved).Devices.Single().Credentials;
        Assert(credentials == saved.Devices[0].Credentials, "matching local job credentials take precedence");
        var unrelated = saved with { Identity = current.Identity! with { ServerId = Guid.NewGuid().ToString() } };
        Assert(NdiJobConfiguratorReader.MergeCredentials(current, unrelated).Devices[0].Credentials != saved.Devices[0].Credentials,
            "another server's credentials must never be used even when job names and IPs match");
        unrelated = saved with { Devices = [saved.Devices[0] with { Id = "different-device-same-ip" }] };
        Assert(NdiJobConfiguratorReader.MergeCredentials(current, unrelated).Devices[0].Credentials != saved.Devices[0].Credentials,
            "IP address cannot substitute for exact device identity");
        credentials = NdiJobConfiguratorReader.MergeCredentials(current, saved with { JobName = "Previous job" }).Devices.Single().Credentials;
        Assert(credentials == new DeviceCredentials("admin", current.JobName), "stale job state must use the current onboarding contract instead of an old password");
        Assert(NdiJobConfiguratorReader.MergeCredentials(current with { Devices = [device with { IsOnboarded = false }] }, null).Devices[0].Credentials is null,
            "unonboarded devices must not receive job credentials");
    }

    public static void RestartClipRestoration()
    {
        var groups = new[] { "Decoder", "LED Wall", "Show" }.Select((name, groupIndex) => new ArenaGroupState(groupIndex + 1, name,
            new[] { "Holding", "Secondary", "Primary" }.Select((layer, layerIndex) => new ArenaLayerState(groupIndex * 3 + layerIndex + 1, layer,
                Enumerable.Range(1, 20).Select(column => (long)(groupIndex * 1000 + layerIndex * 100 + column)).ToArray())).ToArray())).ToArray();
        var state = new ArenaCompositionState("Job", groups.SelectMany(group => group.Layers).ToArray(), groups, []);
        var clips = PostRestartWorker.ResolveRestorationClips(state, ["Decoder"], 5, 2);
        Assert(clips.NdiClipIds.Count == 12 && clips.RouterClipIds.SequenceEqual(new long[] { 207 }), "restore both sources in every decoder/Show layer and the following primary router");
        Assert(!clips.NdiClipIds.Any(id => id is >= 1000 and < 2000), "LED Wall remains empty");
        var manual = PostRestartWorker.ResolveRestorationClips(state, ["Decoder"], 5, 0);
        Assert(manual.NdiClipIds.Count == 0 && manual.RouterClipIds.SequenceEqual(new long[] { 201 }), "manual placement restores only column-one routers");
        ExpectInvalid(() => PostRestartWorker.ResolveRestorationClips(state, ["Other job decoder"], 5, 2), "changed job structure must fail before modifying clips");
        ExpectInvalid(() => PostRestartWorker.ResolveRestorationClips(state, ["Decoder"], 20, 2), "invalid placement must fail before modifying clips");
    }

    public static async Task JobApiResponsesAsync()
    {
        const string health = """{"product":"NDI Job Configurator"}""";
        var cases = new (string[] Responses, bool Valid)[]
        {
            (["null"], false), (["[]"], false), (["{\"product\":123}"], false),
            ([health, "{\"devices\":[null]}"], false),
            ([health, """{"lastJob":{"jobName":"API job"},"devices":[{"id":"test","credentials":{"username":"api-user","password":"api-password"}}]}"""], true)
        };
        foreach (var scenario in cases)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var server = Task.Run(async () =>
            {
                foreach (var response in scenario.Responses)
                {
                    using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                    await using var stream = connection.GetStream();
                    await stream.ReadAsync(new byte[4096], stop.Token);
                    var body = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n"), stop.Token);
                    await stream.WriteAsync(body, stop.Token);
                }
            });
            try
            {
                var snapshot = await NdiJobConfiguratorReader.TryReadApiAsync(address, stop.Token);
                Assert(scenario.Valid ? snapshot?.JobName == "API job" : snapshot is null, "malformed discovery responses must be skipped; valid jobs must still load");
                if (snapshot is not null) Assert(snapshot.Devices.All(device => device.Credentials is null), "network responses must not supply local decoder credentials");
                await server;
            }
            finally
            {
                stop.Cancel();
                try { await server; } catch (OperationCanceledException) { }
            }
        }
    }

    internal static ConfigurationPlan ValidPlan() => new("Test", 3840, 2160, 50, 20, false, true, 5,
        [new DecoderRow { Order = 1, Device = Device("Decoder", "Output") with { Role = "Decoder", Credentials = new("test", "test") }, OutputName = "Decoder", Width = 1920, Height = 1080 }],
        [new EncoderRow { Order = 1, Device = Device("Encoder", "Main"), ArenaSourceName = "Encoder (Main)" }], "Outputs", ".", ".");

    public static void ConfigurationPreflight()
    {
        var valid = ValidPlan();
        ArenaConfigurationOrchestrator.ValidatePlan(valid);
        var invalidPlans = new[]
        {
            valid with { CompositionName = "" }, valid with { CompositionWidth = 0 },
            valid with { Decoders = [] }, valid with { Encoders = [] },
            valid with { TotalColumnCount = 51 }, valid with { FramesPerSecond = 30 },
            valid with { SourceStartColumn = 20 },
            valid with { Decoders = [new DecoderRow { Order = 1, Device = valid.Decoders[0].Device, OutputName = "Show", Width = 1920, Height = 1080 }] },
            valid with { Decoders = [new DecoderRow { Order = 1, Device = valid.Decoders[0].Device with { Credentials = null }, OutputName = "Decoder", Width = 1920, Height = 1080 }] }
        };
        foreach (var invalid in invalidPlans)
            ExpectInvalid(() => ArenaConfigurationOrchestrator.ValidatePlan(invalid), "invalid plans must fail before configuration");
        ArenaConfigurationOrchestrator.ValidatePlan(valid with { AutoPlaceNdiSources = false, Encoders = [] });
        var oversized = new ArenaCompositionState("Original", Enumerable.Range(1, 10).Select(i => new ArenaLayerState(i, "Layer", [])).ToArray(), [], []);
        ExpectInvalid(() => ArenaConfigurationOrchestrator.ValidateInitialComposition(oversized, 3), "surplus layers must fail preflight");
        oversized = oversized with { Layers = [], Groups = Enumerable.Range(1, 4).Select(i => new ArenaGroupState(i, "Group", [])).ToArray() };
        ExpectInvalid(() => ArenaConfigurationOrchestrator.ValidateInitialComposition(oversized, 3), "surplus groups must fail preflight");
    }

    private static void ExpectInvalid(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }

    private static JobDevice Device(string hostname, string channel) => new("id", "192.0.2.1", hostname, "N6", "N6", "Encoder", channel, null, true, "Online");
    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
