using System.Net;
using System.Net.Http;
using System.Text.Json;
using ResolumeConfigurator.Models;
using ResolumeConfigurator.Services;

internal static class MutationBoundaryTests
{
    public static async Task RestartAsync()
    {
        var clipTransport = new ClipFixture();
        using (var api = new ResolumeApiClient(handler: clipTransport))
        {
            api.BeforeMutation = _ => clipTransport.Reads > 1 ? Task.FromException(new InvalidOperationException("Job changed while clip loaded")) : Task.CompletedTask;
            try { await api.ConfigureClipFitAsync(1, CancellationToken.None); throw new Exception("Stale clip configuration succeeded."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Job changed")) { }
            if (clipTransport.Reads != 2 || clipTransport.Writes != 0) throw new Exception("Clip loading crossed a stale mutation boundary.");
        }
        var identity = new JobIdentity(Guid.NewGuid().ToString(), "job", "revision");
        var job = new JobSnapshot("Job", "fixture", DateTimeOffset.Now, [], identity, "192.0.2.5", 1);
        using var status = JsonDocument.Parse("""{"endpointId":"pc","ndiConfiguration":{"preferredInterfaceConfigured":true,"sendGroups":["Job"],"receiveGroups":["Job"],"discoveryServer":"192.0.2.5"}}""");
        foreach (var failure in new[] { "job", "ndi", "unavailable", "none", "later" })
        {
            using var folder = new QaRemediationTests.TempFolder();
            var path = System.IO.Path.Combine(folder.Path, "Job.avc");
            QaRemediationTests.SavedGraph(QaRemediationTests.Graph(100)).Save(path);
            var api = new ArenaFixture();
            var waited = false;
            var validations = 0;
            var worker = new PostRestartWorker(() => api, (_, _) => { waited = true; return Task.CompletedTask; });
            Task Guard(CancellationToken ct)
            {
                validations++;
                if (!waited) throw new Exception("Readiness was checked before the restart wait ended.");
                JobRevisionGuard.Validate(identity, failure == "job" ? job with { Identity = identity with { Revision = "changed" } } : job);
                LocalNdiReadinessService.Validate(status.RootElement, "pc", failure == "ndi" ? job with { DiscoveryServer = "192.0.2.9" } : job);
                if (failure == "unavailable") throw new HttpRequestException("Agent unavailable");
                if (failure == "later" && api.Writes > 0) throw new InvalidOperationException("Job changed during restoration");
                return Task.CompletedTask;
            }
            try
            {
                await worker.RestoreCompositionAsync("Job", ["Decoder"], new(path, 1, 1), CancellationToken.None, Guard);
                if (failure != "none") throw new Exception("Unsafe restoration succeeded.");
                if (api.Writes == 0 || !api.Saved) throw new Exception("Valid restoration did not finish.");
                if (validations != api.Writes) throw new Exception("Restoration redundantly revalidated outside its mutation boundaries.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                if (failure == "none" || api.Writes != (failure == "later" ? 1 : 0) || api.Saved)
                    throw new Exception("Restoration crossed an invalid mutation boundary.", ex);
            }
        }
    }

    public static async Task DecoderAsync()
    {
        foreach (var family in new[] { "N6", "N60" })
        {
            var handler = new DecoderFixture(family);
            var service = new KiloviewDecoderPresetService((_, _, _) => Task.FromResult(new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid") }));
            var device = new JobDevice("id", "192.0.2.1", "Decoder", family, family, "Decoder", "", null, true, "Online", new("fixture", "fixture"));
            var row = new DecoderRow { Order = 1, Device = device, OutputName = "Decoder", Width = 1920, Height = 1080 };
            try
            {
                await service.ConfigureAsync([row], null, CancellationToken.None, _ => handler.Discovered
                    ? Task.FromException(new InvalidOperationException("Job changed while discovery was pending")) : Task.CompletedTask, "192.0.2.10");
                throw new Exception("Stale decoder configuration succeeded.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Job changed")) { }
            if (!handler.Discovered || handler.Writes != 0) throw new Exception("Decoder wrote configuration after a stale source lookup.");
        }
    }

    private sealed class DecoderFixture(string family) : HttpMessageHandler
    {
        internal bool Discovered;
        internal int Writes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path is "/api/codec/preset/get") json = """{"data":[{"id":1}]}""";
            else if (path is "/api/preview/get") json = """{"data":{"position":[]}}""";
            else if (path is "/api/codec/discovery/scan" or "/api/source/groups/list")
            {
                Discovered = true;
                json = family == "N60" ? """{"data":[{"name":"Decoder","channel_name":"Decoder","url":"ndi://192.0.2.10:5961"}]}"""
                    : """{"data":[{"streams":[{"id":"sender","name":"Decoder","url":"ndi://192.0.2.10:5961","address":"192.0.2.10"}]}]}""";
            }
            else { Writes++; throw new Exception("Unexpected configuration request: " + path); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class ArenaFixture : IPostRestartArenaApi
    {
        public Func<CancellationToken, Task>? BeforeMutation { get; set; }
        internal int Writes;
        internal bool Saved;
        private int _reads;
        public Task<ArenaCompositionState> GetCompositionStateAsync(CancellationToken ct)
        {
            if (++_reads == 1) throw new HttpRequestException("Arena is restarting");
            return Task.FromResult(QaRemediationTests.Graph(100));
        }
        private async Task Write() { if (BeforeMutation is not null) await BeforeMutation(CancellationToken.None); Writes++; }
        public Task ConfigureClipFitAsync(long id, CancellationToken ct) => Write();
        public Task ConfigureVideoRouterFitAsync(long id, CancellationToken ct) => Write();
        public Task ConnectClipAsync(long id, CancellationToken ct) => Write();
        public Task UpdateClipThumbnailAsync(long id, CancellationToken ct) => Write();
        public async Task SaveCompositionAsync(string path, CancellationToken ct) { await Write(); Saved = true; }
        public void Dispose() { }
    }

    private sealed class ClipFixture : HttpMessageHandler
    {
        internal int Reads, Writes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get) { Writes++; throw new Exception("Unexpected clip write"); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(++Reads == 1 ? "{}" : """{"video":{"resize":{"id":123}}}""") });
        }
    }
}
