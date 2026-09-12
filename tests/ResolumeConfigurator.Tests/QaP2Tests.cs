using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ResolumeConfigurator.Models;
using ResolumeConfigurator.Services;
using static QaRemediationTests;

internal static class QaP2Tests
{
    private static void Assert(bool condition, string message) => RegressionTests.Assert(condition, message);
    internal static async Task PresetOwnershipAsync()
    {
        foreach (var family in new[] { "N6", "N60" })
        foreach (var mode in new[] { "foreign-free", "owned", "foreign-full" })
        {
            using var transport = new PresetTransport(family);
            if (mode == "owned") transport.Rows[1] = new("LOCAL (Arena - Decoder)", "ndi://192.0.2.10:5961");
            if (mode == "foreign-full")
                for (var slot = 2; slot <= 10; slot++) transport.Rows[slot] = new("Other", "ndi://192.0.2.99:5961");
            var service = new KiloviewDecoderPresetService((_, _, _) => Task.FromResult(new HttpClient(transport) { BaseAddress = new Uri("http://fixture.invalid") }));
            var original = RegressionTests.ValidPlan().Decoders.Single();
            var row = new DecoderRow { Order = 1, Device = original.Device with { Family = family, Model = family }, OutputName = "Decoder", Width = 1920, Height = 1080 };
            DecoderPresetResult? result = null;
            var rejected = false;
            try { result = (await service.ConfigureAsync([row], null, CancellationToken.None, _ => Task.CompletedTask, "192.0.2.10")).Single(); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected == (mode == "foreign-full"), "full foreign banks must fail while free/owned slots remain usable");
            if (mode == "owned") Assert(result is { Slot: 1, ReusedExistingSlot: true }, "verified sender preset must be reused");
            else
            {
                Assert(!transport.ForeignModified && transport.Rows[1].Url == "ndi://192.0.2.99:5961", "foreign preset must survive configuration");
                if (mode == "foreign-free") Assert(result is { Slot: 2, ReusedExistingSlot: false }, "an available slot must be used for the pinned sender");
            }
        }
        Assert(KiloviewDecoderPresetService.SelectN6Slot([new(1, "LOCAL (Arena - Decoder)")], "Decoder", out var reused, "192.0.2.10") == 0 && !reused, "missing ownership URL must not permit reuse");
        try
        {
            KiloviewDecoderPresetService.SelectN6Slot([new(1, "LOCAL (Arena - Decoder)", "ndi://192.0.2.10:5961"), new(2, "LOCAL (Arena - Decoder)", "ndi://192.0.2.10:5962")], "Decoder", out _, "192.0.2.10");
            throw new Exception("ambiguous owned presets were accepted");
        }
        catch (InvalidOperationException) { }
    }

    internal static async Task PreferenceConcurrencyAsync()
    {
        using var folder = new TempFolder();
        var paths = ArenaPaths.FromRoot(folder.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SimpleOutputPreference)!);
        File.WriteAllText(paths.SimpleOutputPreference, "<SimpleSetup advancedModeEnabled=\"1\"><Outputs/></SimpleSetup>");
        var plan = RegressionTests.ValidPlan() with { CompositionDirectory = paths.Compositions, PresetDirectory = paths.AdvancedOutputPresets };
        var files = await ConfigurationFiles.PrepareAsync(plan, paths, new("Arena", 7, 27, 0, 0), CancellationToken.None);
        var guards = 0;
        var rejected = false;
        try
        {
            await files.CommitAsync(_ =>
            {
                if (++guards == 3) File.WriteAllText(paths.SimpleOutputPreference, "<SimpleSetup userEdit=\"preserve\"><Outputs/></SimpleSetup>");
                return Task.CompletedTask;
            }, CancellationToken.None);
        }
        catch (IOException) { rejected = true; }
        Assert(rejected && XDocument.Load(paths.SimpleOutputPreference).Root?.Attribute("userEdit")?.Value == "preserve", "a preference edit during the guard must be preserved and reported");
    }

    internal static void AdvancedOutputEnabled()
    {
        using var folder = new TempFolder();
        var file = Path.Combine(folder.Path, "SimpleOutput.xml");
        foreach (var initial in new[] { "<SimpleSetup advancedModeEnabled=\"0\"><Outputs><Keep/></Outputs></SimpleSetup>", "<SimpleSetup><Outputs><Keep/></Outputs></SimpleSetup>" })
        {
            File.WriteAllText(file, initial);
            foreach (var sharing in new[] { false, true })
            {
                var result = SimpleOutputConfigurationService.CreateDocument(file, sharing, 1920, 1080);
                Assert(result.Root?.Attribute("advancedModeEnabled")?.Value == "1", "advanced output must be enabled for an existing preference file");
                Assert(result.Descendants("Keep").Any(), "unrelated simple output devices must survive");
                Assert(result.Descendants("OutputDeviceNDI").Any() == sharing, "composition sharing remains independent");
            }
        }
    }

    internal static async Task SupportedSchemaAsync()
    {
        foreach (int? schema in new int?[] { null, 0, 2, 1 })
        {
            await using var server = new JobServer { Schema = schema };
            var rejected = false;
            try { await JobRevisionGuard.RefreshAsync(server.Address, JobServer.Identity, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected == (schema != 1), "missing/unsupported schema must fail closed while schema 1 remains valid");
        }
        await using var changing = new JobServer();
        var snapshot = await new NdiJobConfiguratorReader(changing.Address).ReadAsync(CancellationToken.None, includeLocalCredentials: false);
        changing.Schema = 2;
        var driftRejected = false;
        try { await JobRevisionGuard.RefreshAsync(changing.Address, snapshot.Identity, CancellationToken.None); }
        catch (InvalidOperationException) { driftRejected = true; }
        Assert(driftRejected, "schema drift must stop a previously valid revision");
    }

    internal static async Task SaveEvidenceAsync()
    {
        using var folder = new TempFolder();
        const string original = "<Composition marker=\"old\"/>";
        foreach (var mode in new[] { "fresh", "identical", "changed-same-time", "old", "malformed" })
        {
            var path = Path.Combine(folder.Path, mode + ".avc");
            var originalTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            if (mode != "fresh") { File.WriteAllText(path, original); File.SetLastWriteTimeUtc(path, originalTime); }
            using var transport = new Handler(async (request, ct) =>
            {
                if (request.Method == HttpMethod.Post && mode != "old")
                {
                    await File.WriteAllTextAsync(path, mode == "malformed" ? "<Composition" : mode == "identical" ? original : "<Composition marker=\"new\"/>", ct);
                    var ticks = DateTime.UtcNow.Ticks;
                    File.SetLastWriteTimeUtc(path, mode == "fresh" ? new(ticks - ticks % (2 * TimeSpan.TicksPerSecond), DateTimeKind.Utc) : originalTime);
                }
                return "{}";
            });
            using var api = new ResolumeApiClient(handler: transport, completionTimeout: TimeSpan.FromMilliseconds(700));
            var rejected = false;
            try { await api.SaveCompositionAsync(path, CancellationToken.None); }
            catch (Exception ex) when (ex is TimeoutException or IOException) { rejected = true; }
            Assert(rejected == (mode is "old" or "malformed"), "coarse/fresh saves must verify; unchanged old or malformed files must not");
            if (mode == "old") Assert(File.ReadAllText(path) == original, "failed dispatch/completion must preserve the prior file");
            if (mode == "malformed") Assert(Directory.EnumerateFiles(folder.Path, ".configurator-save-*.previous").Any(file => File.ReadAllText(file) == original), "failed replacement must retain the prior complete bytes");
        }
        foreach (var cancel in new[] { false, true })
        {
            var path = Path.Combine(folder.Path, "dispatch-failure.avc");
            File.WriteAllText(path, original);
            using var cancellation = new CancellationTokenSource();
            using var transport = new Handler((_, token) =>
            {
                if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                throw new HttpRequestException("save dispatch failed");
            });
            using var api = new ResolumeApiClient(handler: transport);
            try { await api.SaveCompositionAsync(path, cancellation.Token); throw new Exception("save failure was ignored"); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
            Assert(File.ReadAllText(path) == original, "request failure/cancellation must restore the prior composition when no new file was written");
        }
    }

    internal sealed class JobServer : IAsyncDisposable
    {
        internal static readonly JobIdentity Identity = new("c813bbca-25d7-4a2c-b0f9-56d8d7ece795", "job", "revision");
        internal int? Schema = 1;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(10));
        private readonly Task _serving;
        internal string Address { get; }
        internal JobServer()
        {
            _listener.Start();
            Address = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _serving = Task.Run(async () =>
            {
                try
                {
                    while (!_lifetime.IsCancellationRequested)
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                        await using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        var request = await reader.ReadLineAsync(_lifetime.Token);
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_lifetime.Token))) { }
                        var state = new Dictionary<string, object> { ["serverId"] = Identity.ServerId, ["jobId"] = Identity.JobId, ["jobRevision"] = Identity.Revision,
                            ["lastJob"] = new { jobName = "Job" }, ["devices"] = Array.Empty<object>() };
                        if (Schema.HasValue) state["integrationSchemaVersion"] = Schema.Value;
                        var body = Encoding.UTF8.GetBytes(request!.Contains("/api/health") ? "{\"product\":\"NDI Job Configurator\"}" : JsonSerializer.Serialize(state));
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), _lifetime.Token);
                        await stream.WriteAsync(body, _lifetime.Token);
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            });
        }
        public async ValueTask DisposeAsync() { _lifetime.Cancel(); await _serving; _listener.Stop(); _lifetime.Dispose(); }
    }

    private sealed class PresetTransport(string family) : HttpMessageHandler
    {
        internal sealed record Row(string Name, string Url);
        internal readonly Dictionary<int, Row> Rows = new() { [1] = new("OTHER-PC (Arena - Decoder)", "ndi://192.0.2.99:5961") };
        internal bool ForeignModified;
        private const string Name = "LOCAL (Arena - Decoder)", Url = "ndi://192.0.2.10:5961";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            object data;
            if (path == "/api/codec/preset/get") data = Rows.Select(row => (object)new { id = row.Key, name = row.Value.Name, channel_name = "Arena - Decoder", url = row.Value.Url }).Concat(Rows.ContainsKey(2) ? [] : new object[] { new { id = 2 } }).ToArray();
            else if (path == "/api/preview/get") data = new { position = Rows.Select(row => new { id = row.Key, stream_name = row.Value.Name, stream_url = row.Value.Url, stream_id = "sender" }).ToArray() };
            else if (path == "/api/codec/discovery/scan") data = new[] { new { name = Name, channel_name = "Arena - Decoder", url = Url, port = 5961 } };
            else if (path == "/api/source/groups/list") data = new[] { new { streams = new[] { new { id = "sender", name = Name, url = Url, address = "192.0.2.10" } } } };
            else if (path is "/api/codec/decode/get" or "/api/decoder/current/get.json") data = new { name = Name, channel_name = "Arena - Decoder", url = Url };
            else
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (path == "/api/codec/preset/remove") { var id = body.RootElement.GetProperty("id").GetInt32(); ForeignModified |= id == 1; Rows.Remove(id); }
                else if (path == "/api/codec/preset/add") { var id = body.RootElement.GetProperty("position").GetInt32(); ForeignModified |= id == 1; Rows[id] = new(Name, Url); }
                else if (path == "/api/preview/source/modify") { var to = body.RootElement.GetProperty("to"); var id = to.TryGetProperty("pos_id", out var pos) ? pos.GetInt32() : 2; ForeignModified |= id == 1; Rows[id] = new(Name, Url); }
                else if (path is not ("/api/codec/decode/add" or "/api/decoder/current/set.json")) throw new InvalidOperationException("Unexpected fixture request: " + path + family);
                data = new { };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { result = "ok", data }), Encoding.UTF8, "application/json") };
        }
    }
}
