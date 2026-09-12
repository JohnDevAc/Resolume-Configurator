using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using ResolumeConfigurator.Models;
using ResolumeConfigurator.Services;
using static RegressionTests;

internal static class QaRemediationTests
{
    private static void Assert(bool condition, string message) => RegressionTests.Assert(condition, message);
    internal static void SourceIdentity()
    {
        var device = ValidPlan().Encoders[0].Device with { Hostname = "TT-001", NdiChannelName = "Main" };
        ArenaSource Source(string name) => new(name, name, "NDI Servers");
        Assert(SourceMatcher.BestMatch(device, [Source("TT-002 (Main)")]) is null, "a channel cannot override a contradictory host");
        Assert(SourceMatcher.BestMatch(device, [Source("TT-001 (Preview)")]) is null, "a host cannot override a contradictory channel");
        Assert(SourceMatcher.BestMatch(device, [Source("TT-001")]) is null, "both known components are required");
        Assert(SourceMatcher.BestMatch(device, [Source("tt-001 (main)")]) is not null, "full pair is case insensitive");
        Assert(SourceMatcher.BestMatch(device with { NdiChannelName = "TT-001 (Main)" }, [Source("TT-001 (Main)")]) is not null, "full advertised name is supported in channel field");
        Assert(SourceMatcher.BestMatch(device with { Hostname = "Κάμερα", NdiChannelName = "Main (SDI)" }, [Source("Κάμερα (Main (SDI))")]) is not null, "Unicode and channel parentheses retain identity");
        Assert(SourceMatcher.BestMatch(device with { NdiChannelName = "" }, [Source("TT-001 (Main)")]) is not null, "a known host alone can identify an unambiguous source");
        Assert(SourceMatcher.BestMatch(device with { Hostname = "" }, [Source("TT-001 (Main)")]) is not null, "known channel alone can identify an unambiguous source");
        Assert(SourceMatcher.BestMatch(device with { Hostname = "" }, [Source("TT-001 (Main)"), Source("TT-002 (Main)")]) is null, "unknown-host ambiguity requires manual selection");
        Assert(SourceMatcher.BestMatch(device, [new("TT-001 (Main)", "TT-002 (Main)", "NDI Servers")]) is null, "conflicting name/token metadata cannot authorize a match");
    }

    internal static async Task RestartIdentityAsync()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "Job.avc");
        var expected = Graph(100);
        var wrong = Graph(10000);
        foreach (var scenario in new[] { "wrong", "changed", "valid" })
        {
            SavedGraph(expected).Save(path);
            var api = new GraphApi(scenario == "wrong" ? wrong : expected);
            if (scenario == "changed") api.AfterWrite = () => api.State = wrong;
            var worker = new PostRestartWorker(() => api, Task.Delay);
            using var timeout = new CancellationTokenSource(scenario == "wrong" ? 120 : 5000);
            var rejected = false;
            try { await worker.RestoreCompositionAsync("Job", ["Decoder"], new(path, 1, 1), timeout.Token, _ => Task.CompletedTask); }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { rejected = true; }
            if (scenario == "valid") Assert(!rejected && api.Writes == 15 && api.Saved, "valid pinned graph still restores and saves");
            else
            {
                Assert(rejected && api.Writes == (scenario == "wrong" ? 0 : 1) && !api.Saved, "wrong/replaced graph must stop before any further write");
                Assert(ArenaCompositionSynchronizationService.MatchesSavedStructure(XDocument.Load(path), expected), "expected composition file must survive a rejected graph");
            }
        }
    }

    internal static ArenaCompositionState Graph(long offset)
    {
        var groups = new[] { "Decoder", "LED Wall", "Show" }.Select((name, i) => new ArenaGroupState(offset + i, name,
            new[] { "Holding", "Secondary", "Primary" }.Select((layer, j) => new ArenaLayerState(offset + 10 + i * 3 + j, layer,
                [offset + 100 + i * 10 + j * 2, offset + 101 + i * 10 + j * 2])).ToArray())).ToArray();
        return new("Job", groups.SelectMany(group => group.Layers).ToArray(), groups, [1, 2]);
    }
    internal static XDocument SavedGraph(ArenaCompositionState state) => new(new XElement("Composition", new XAttribute("numColumns", state.ColumnIds.Count),
        state.Groups.Select(group => new XElement("Group", new XAttribute("uniqueId", group.Id))),
        state.Groups.SelectMany((group, index) => group.Layers.Select(layer => new XElement("Layer", new XAttribute("uniqueId", layer.Id), new XAttribute("layerGroup", index)))),
        new XElement("Deck", state.Layers.SelectMany((layer, li) => layer.ClipIds.Select((id, ci) => new XElement("Clip", new XAttribute("uniqueId", id),
            new XAttribute("layerIndex", li), new XAttribute("columnIndex", ci)))))));

    internal sealed class GraphApi(ArenaCompositionState state) : IPostRestartArenaApi
    {
        internal ArenaCompositionState State = state;
        internal int Writes;
        internal bool Saved;
        internal Action? AfterWrite;
        public Func<CancellationToken, Task>? BeforeMutation { get; set; }
        public Task<ArenaCompositionState> GetCompositionStateAsync(CancellationToken ct) => Task.FromResult(State);
        private async Task Write(CancellationToken ct) { if (BeforeMutation is not null) await BeforeMutation(ct); Writes++; AfterWrite?.Invoke(); }
        public Task ConfigureClipFitAsync(long id, CancellationToken ct) => Write(ct);
        public Task ConfigureVideoRouterFitAsync(long id, CancellationToken ct) => Write(ct);
        public Task ConnectClipAsync(long id, CancellationToken ct) => Write(ct);
        public Task UpdateClipThumbnailAsync(long id, CancellationToken ct) => Write(ct);
        public async Task SaveCompositionAsync(string path, CancellationToken ct) { await Write(ct); SavedGraph(State).Save(path); Saved = true; }
        public void Dispose() { }
    }
    internal sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "resolume-qa-fix-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<string>> handler) : HttpMessageHandler
    {
        public int Writes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get) Writes++;
            return new(HttpStatusCode.OK) { Content = new StringContent(await handler(request, ct), Encoding.UTF8, "application/json") };
        }
    }
}
