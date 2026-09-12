using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ResolumeConfigurator.Models;
using ResolumeConfigurator.Services;
using static RegressionTests;

internal static class AuditRegressionTests
{
    private static void Assert(bool condition, string message) => RegressionTests.Assert(condition, message);
    public static void SourceIdentities()
    {
        var device = ValidPlan().Encoders[0].Device with { Hostname = "TT-001", NdiChannelName = "TT-001" };
        Assert(SourceMatcher.BestMatch(device, [new("TT-0010", "TT-0010", "NDI Servers")]) is null, "numeric prefixes cannot auto-match");
        Assert(SourceMatcher.BestMatch(device with { NdiChannelName = "Main" }, [new("x", "TT-001 (Main)", "NDI Servers")])?.IdString == "x", "complete NDI host and channel match");
        Assert(SourceMatcher.BestMatch(device with { Hostname = "Camerá-1", NdiChannelName = "" },
            [new("x", "Camer-1", "NDI Servers")]) is null, "removing Unicode or punctuation cannot turn different identities into a match");
        Assert(SourceMatcher.BestMatch(device with { Hostname = "Κάμερα", NdiChannelName = "" },
            [new("x", "Κάμερα (Main)", "NDI Servers")])?.IdString == "x", "complete Unicode host identities are supported");
        Assert(SourceMatcher.BestMatch(device, [new("a", "Host A (TT-001)", "NDI Servers"), new("b", "Host B (TT-001)", "NDI Servers")]) is null,
            "ambiguous channels cannot auto-match");
        Assert(SourceMatcher.BestMatch(device with { Hostname = "Host A" },
            [new("a", "Host A (TT-001)", "NDI Servers"), new("b", "Host A (Other)", "NDI Servers")])?.IdString == "a",
            "a complete host and channel pair outranks a shared host");
        var plan = ValidPlan();
        var duplicate = new EncoderRow { Order = 2, Device = device, ArenaSourceName = plan.Encoders[0].ArenaSourceName };
        Throws<InvalidOperationException>(() => ArenaConfigurationOrchestrator.ValidatePlan(plan with { Encoders = [plan.Encoders[0], duplicate] }));
        Throws<InvalidOperationException>(() => KiloviewDecoderPresetService.IsN60Device(device with { Family = "Unknown" }));
        Throws<InvalidOperationException>(() => ArenaConfigurationOrchestrator.ValidateProduct(new("Arena", 8, 0, 0, 0)));
    }

    public static void SenderIdentity()
    {
        using var sources = JsonDocument.Parse("""[{"name":"LOCAL (Arena - Decoder)","url":"ndi://192.0.2.10:5961","port":5961},{"name":"OTHER (Arena - Decoder)","url":"ndi://192.0.2.20:5999","port":5999}]""");
        var match = KiloviewDecoderPresetService.SelectOutputSource(sources.RootElement.EnumerateArray(), "Decoder", "192.0.2.10");
        Assert(match.GetProperty("url").GetString() == "ndi://192.0.2.10:5961", "higher port on another host cannot win");
        Assert(KiloviewDecoderPresetService.SelectOutputSource(sources.RootElement.EnumerateArray(), "Decoder", "192.0.2.30").ValueKind == JsonValueKind.Undefined,
            "no sender on the verified host means no match");
        using var duplicate = JsonDocument.Parse("""[{"name":"Decoder","url":"ndi://192.0.2.10:5961"},{"name":"Decoder","url":"ndi://192.0.2.10:5962"}]""");
        Throws<InvalidOperationException>(() => KiloviewDecoderPresetService.SelectOutputSource(duplicate.RootElement.EnumerateArray(), "Decoder", "192.0.2.10"));
        using var wrong = JsonDocument.Parse("""{"channel_name":"Decoder","url":"ndi://192.0.2.20:5961"}""");
        Assert(!KiloviewDecoderPresetService.IsActiveN60Source(wrong.RootElement, "Decoder", "ndi://192.0.2.10:5961"), "same channel on wrong host cannot verify N60 activation");
        using var correct = JsonDocument.Parse("""{"channel_name":"Decoder","url":"ndi://192.0.2.10:5961"}""");
        Assert(KiloviewDecoderPresetService.IsActiveN60Source(correct.RootElement, "Decoder", "ndi://192.0.2.10:5961"), "matching sender and channel verifies activation");
    }

    public static async Task CompletionGuardsAsync()
    {
        foreach (var thumbnail in new[] { true, false })
        {
            using var handler = new Handler((_, _) => Task.FromResult("""{"thumbnail":{"last_update":"same","is_default":false},"connected":{"value":"Connected"}}"""));
            using var api = new ResolumeApiClient(handler: handler, completionTimeout: TimeSpan.FromSeconds(1));
            api.BeforeMutation = async ct => { await Task.Delay(350, ct); throw new InvalidOperationException("Job changed"); };
            await ThrowsAsync<InvalidOperationException>(() => thumbnail ? api.UpdateClipThumbnailAsync(1, CancellationToken.None) : api.ConnectClipAsync(1, CancellationToken.None));
            Assert(handler.Writes == 0, "guard failure must propagate without dispatch");
            var validated = false;
            api.BeforeMutation = async ct => { await Task.Delay(350, ct); validated = true; };
            handler.OnWrite = () => Assert(validated, "validation must finish before dispatch");
            if (thumbnail) await api.UpdateClipThumbnailAsync(1, CancellationToken.None);
            else await api.ConnectClipAsync(1, CancellationToken.None);
            Assert(handler.Writes == 1, "successful completion must follow a dispatched POST");
            using var canceled = new CancellationTokenSource(20);
            await ThrowsAsync<OperationCanceledException>(() => thumbnail ? api.UpdateClipThumbnailAsync(1, canceled.Token) : api.ConnectClipAsync(1, canceled.Token));
            Assert(handler.Writes == 1, "caller cancellation during guard must not dispatch");
        }
        using var pending = new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post) await Task.Delay(Timeout.Infinite, ct);
            return """{"thumbnail":{"last_update":"unchanged","is_default":false}}""";
        });
        using var stalledApi = new ResolumeApiClient(handler: pending, completionTimeout: TimeSpan.FromMilliseconds(350));
        await ThrowsAsync<TimeoutException>(() => stalledApi.UpdateClipThumbnailAsync(1, CancellationToken.None));
        Assert(pending.Writes == 1, "an unchanged old thumbnail cannot confirm a stalled update");
    }

    private const string SavedGraph = """<Composition numColumns="1"><Group uniqueId="10"/><Layer uniqueId="20" layerGroup="0"/><Deck><Clip uniqueId="30" layerIndex="0" columnIndex="0"/></Deck></Composition>""";
    private const string LiveGraph = """{"name":{"value":"Job"},"layers":[{"id":20,"name":{"value":"Primary"},"clips":[{"id":30}]}],"layergroups":[{"id":10,"name":{"value":"Show"},"layers":[{"id":20,"name":{"value":"Primary"},"clips":[{"id":30}]}]}],"columns":[{"id":1}]}""";

    public static async Task SaveAndOpenAsync()
    {
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(directory.Path, "Job.avc");
        using (var api = new ResolumeApiClient(handler: new Handler((_, _) => Task.FromResult("{}")), completionTimeout: TimeSpan.FromMilliseconds(220)))
            await ThrowsAsync<TimeoutException>(() => api.SaveCompositionAsync(file, CancellationToken.None));
        Assert(!File.Exists(file), "a fast save response without a file must fail");
        File.WriteAllText(file, SavedGraph);
        foreach (var stalled in new[] { false, true })
        {
            using var handler = new Handler(async (request, ct) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    await Task.Delay(30, ct);
                    File.WriteAllText(file, SavedGraph);
                    if (stalled) await Task.Delay(Timeout.Infinite, ct);
                }
                return "{}";
            });
            using var api = new ResolumeApiClient(handler: handler, completionTimeout: TimeSpan.FromSeconds(2));
            var timer = Stopwatch.StartNew();
            await api.SaveCompositionAsync(file, CancellationToken.None);
            Assert(timer.ElapsedMilliseconds >= 400, "both fast and stalled save responses must wait for a settled file");
        }
        using (var api = new ResolumeApiClient(handler: new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get ? LiveGraph.Replace("30", "31") : "{}")),
            completionTimeout: TimeSpan.FromMilliseconds(220)))
            await ThrowsAsync<TimeoutException>(() => api.OpenCompositionAsync(file, "Job", CancellationToken.None));
        using (var api = new ResolumeApiClient(handler: new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get ? LiveGraph : "{}")),
            completionTimeout: TimeSpan.FromSeconds(2)))
            await api.OpenCompositionAsync(file, "Job", CancellationToken.None);
    }

    public static async Task FilesAndNamesAsync()
    {
        using var directory = new TemporaryDirectory();
        foreach (var raw in new[] { "CON", "NUL.txt", "COM¹", "A/B", new string('A', 256), "..." })
        {
            var name = ArenaPaths.SafeFileName(raw, "Job");
            Assert(name.Length <= 120 && name == ArenaPaths.SafeFileName(raw, "Job"), "filenames must be bounded and stable");
            var path = Path.Combine(directory.Path, name + ".avc");
            File.WriteAllText(path, "fixture");
            Assert(File.ReadAllText(path) == "fixture", "sanitized names must be writable ordinary files");
        }
        Assert(ArenaPaths.SafeFileName("A/B", "Job") != ArenaPaths.SafeFileName("A_B", "Job"), "sanitization must preserve distinct identities");
        Assert(ArenaPaths.OutputPath(directory.Path, new string('A', 256), ".avc").Length <= 240, "Arena paths must budget for the full directory");
        Throws<InvalidOperationException>(() => ArenaPaths.OutputPath(System.IO.Path.Combine(directory.Path, new string('B', 200)), "Job", ".avc"));
        var target = Path.Combine(directory.Path, "atomic.xml");
        File.WriteAllText(target, "<Original/>");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => AtomicFile.WriteXmlAsync(target, XDocument.Parse("<Replacement/>"), canceled.Token, true));
        Assert(File.ReadAllText(target) == "<Original/>", "canceled writes must not truncate a destination");
        await AtomicFile.WriteXmlAsync(target, XDocument.Parse("<One/>"), CancellationToken.None, true);
        await AtomicFile.WriteXmlAsync(target, XDocument.Parse("<Two/>"), CancellationToken.None, true);
        Assert(Directory.GetFiles(directory.Path, "*.bak").Length == 2, "rapid consecutive replacements must keep distinct backups");
        var saved = Environment.GetEnvironmentVariable("RESOLUME_ARENA_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("RESOLUME_ARENA_DATA_DIR", "relative-path");
            Throws<InvalidOperationException>(() => ArenaPaths.Resolve());
            Environment.SetEnvironmentVariable("RESOLUME_ARENA_DATA_DIR", directory.Path);
            Assert(ArenaPaths.Resolve().Root == directory.Path, "an explicit absolute data directory must be respected");
        }
        finally { Environment.SetEnvironmentVariable("RESOLUME_ARENA_DATA_DIR", saved); }
    }

    public static async Task FilePreflightAsync()
    {
        using var directory = new TemporaryDirectory();
        var paths = ArenaPaths.FromRoot(directory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SimpleOutputPreference)!);
        var plan = ValidPlan() with { CompositionDirectory = paths.Compositions, PresetDirectory = paths.AdvancedOutputPresets };
        var product = new ArenaProduct("Arena", 7, 27, 1, 1);
        File.WriteAllText(paths.SimpleOutputPreference, "<broken");
        await ThrowsAsync<InvalidDataException>(() => ConfigurationFiles.PrepareAsync(plan, paths, product, CancellationToken.None));
        Assert(!File.Exists(paths.AdvancedOutputPreference), "malformed SimpleOutput must fail before replacing AdvancedOutput");
        File.WriteAllText(paths.SimpleOutputPreference, "<SimpleSetup><Outputs/></SimpleSetup>");
        var files = await ConfigurationFiles.PrepareAsync(plan, paths, product, CancellationToken.None);
        Assert(!File.Exists(files.PresetFile) && !File.Exists(paths.AdvancedOutputPreference), "preparation must stage outputs without committing");
        File.WriteAllText(paths.SimpleOutputPreference, "<SimpleSetup changed=\"1\"/>");
        await ThrowsAsync<IOException>(() => files.CommitAsync(_ => Task.CompletedTask, CancellationToken.None));
        Assert(!File.Exists(files.PresetFile), "a changed preference must block the whole output commit");
        files = await ConfigurationFiles.PrepareAsync(plan, paths, product, CancellationToken.None);
        await files.CommitAsync(_ => Task.CompletedTask, CancellationToken.None);
        Assert(AdvancedOutputActivator.PreferenceMatches(paths.AdvancedOutputPreference, ["Show", "LED Wall", "Decoder"]), "staged output must be applied completely");
        Assert(File.Exists(Path.Combine(files.OperationDirectory, "operation.json")), "recovery manifest must be retained");
        using (var locked = new FileStream(files.PresetFile, FileMode.Open, FileAccess.Read, FileShare.None))
            await ThrowsAsync<IOException>(() => ConfigurationFiles.PrepareAsync(plan, paths, product, CancellationToken.None));

        var before = File.ReadAllText(files.PresetFile);
        files = await ConfigurationFiles.PrepareAsync(plan, paths, product, CancellationToken.None);
        FileStream? lockDuringCommit = null;
        var validations = 0;
        try
        {
            await ThrowsAsync<IOException>(() => files.CommitAsync(_ =>
            {
                if (++validations == 2) lockDuringCommit = new FileStream(paths.AdvancedOutputPreference, FileMode.Open, FileAccess.Read, FileShare.None);
                return Task.CompletedTask;
            }, CancellationToken.None));
        }
        finally { lockDuringCommit?.Dispose(); }
        Assert(XNode.DeepEquals(XDocument.Parse(before), XDocument.Load(files.PresetFile)), "a file commit failure must roll back earlier output replacements");

        files = await ConfigurationFiles.PrepareAsync(plan, paths, product, CancellationToken.None);
        validations = 0;
        await ThrowsAsync<IOException>(() => files.CommitAsync(_ => ++validations == 2
            ? Task.FromException(new IOException("Agent state unavailable")) : Task.CompletedTask, CancellationToken.None));
        using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(files.OperationDirectory, "operation.json")));
        Assert(record.RootElement.GetProperty("Files")[0].GetProperty("Status").GetString() == "committed",
            "a failed readiness guard must stop further writes, including automatic rollback");
    }

    public static async Task WorkerResultsAsync()
    {
        using var directory = new TemporaryDirectory();
        var result = Path.Combine(directory.Path, "worker.json");
        const string id = "operation";
        await ThrowsAsync<InvalidOperationException>(() => WorkerCompletion.WaitAsync(result, id, _ => Task.FromResult(1), () => { }, TimeSpan.FromSeconds(1), CancellationToken.None));
        await AtomicFile.WriteJsonAsync(result, new WorkerOutcome("other", true, null, []), CancellationToken.None);
        await ThrowsAsync<InvalidDataException>(() => WorkerCompletion.WaitAsync(result, id, _ => Task.FromResult(0), () => { }, TimeSpan.FromSeconds(1), CancellationToken.None));
        await AtomicFile.WriteJsonAsync(result, new WorkerOutcome(id, false, "Detailed decoder failure", []), CancellationToken.None);
        var failure = await ThrowsAsync<InvalidOperationException>(() => WorkerCompletion.WaitAsync(result, id, _ => Task.FromResult(1), () => { }, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert(failure.Message == "Detailed decoder failure", "the complete helper error must reach the caller");
        await AtomicFile.WriteJsonAsync(result, new WorkerOutcome(id, true, null, [new("Decoder", "N6", 1, false)]), CancellationToken.None);
        var decoders = await WorkerCompletion.WaitAsync(result, id, _ => Task.FromResult(0), () => { }, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert(decoders.Count == 1, "success requires a matching result and successful process exit");
        var stopped = false;
        await ThrowsAsync<TimeoutException>(() => WorkerCompletion.WaitAsync(result, id, async ct =>
        {
            if (!stopped) await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }, () => stopped = true, TimeSpan.FromMilliseconds(30), CancellationToken.None));
        Assert(stopped, "a deadline must stop the helper before releasing the operation");
        var host = ArenaRestartService.CreateCompanionStartInfo(@"C:\Program Files\dotnet\dotnet.exe", @"C:\Apps\Configurator.dll");
        Assert(host.ArgumentList.Single() == @"C:\Apps\Configurator.dll", "managed host launches must include the app assembly");
    }

    public static async Task AgentStateRetryAsync()
    {
        using var directory = new TemporaryDirectory();
        var stateFile = Path.Combine(directory.Path, "agent.json");
        File.WriteAllText(stateFile, "{");
        var writer = Task.Run(async () => { await Task.Delay(30); await File.WriteAllTextAsync(stateFile, "{\"schemaVersion\":1}"); });
        using var state = await LocalNdiReadinessService.ReadStateAsync(stateFile, CancellationToken.None);
        await writer;
        Assert(state.RootElement.GetProperty("schemaVersion").GetInt32() == 1, "short-lived partial Agent writes should recover");
        File.WriteAllText(stateFile, "{");
        File.WriteAllText(stateFile + ".bak", "{\"schemaVersion\":1}");
        await ThrowsAsync<JsonException>(() => LocalNdiReadinessService.ReadStateAsync(stateFile, CancellationToken.None));
    }

    public static async Task OperationalTimeoutAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
                if (index == 1) await Task.Delay(1000, deadline.Token);
                var body = Encoding.UTF8.GetBytes(index == 0 ? """{"product":"NDI Job Configurator"}""" : """{"integrationSchemaVersion":1,"lastJob":{"jobName":"Slow but reachable"},"devices":[]}""");
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n"), deadline.Token);
                await stream.WriteAsync(body, deadline.Token);
            }
        });
        var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var job = await new NdiJobConfiguratorReader(address).ReadAsync(deadline.Token, includeLocalCredentials: false);
        await server;
        Assert(job.JobName == "Slow but reachable", "a selected server must have a longer operational deadline than a discovery probe");
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T ex) { return ex; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T ex) { return ex; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<string>> respond) : HttpMessageHandler
    {
        public int Writes;
        public Action? OnWrite;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get) { OnWrite?.Invoke(); Writes++; }
            return new(HttpStatusCode.OK) { Content = new StringContent(await respond(request, ct)) };
        }
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "resolume-audit-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
