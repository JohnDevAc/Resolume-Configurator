using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public sealed class ResolumeApiClient : IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public ResolumeApiClient(string baseAddress = "http://127.0.0.1:8080/api/v1/", TimeSpan? timeout = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = timeout ?? TimeSpan.FromSeconds(60) };
        // Arena is deliberately killed and relaunched during one app workflow.
        // Do not retain keep-alive sockets belonging to the previous process;
        // a stale accepted connection can otherwise outlive the old listener
        // while the replacement webserver is already healthy.
        _http.DefaultRequestHeaders.ConnectionClose = true;
    }

    public async Task<ArenaProduct> GetProductAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "product");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "read Arena product information", ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[4096];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), ct);
            if (read == 0) break;
            length += read;
            var product = TryParseProduct(buffer.AsSpan(0, length));
            if (product is not null) return product;
        }
        throw new InvalidDataException("Arena returned an incomplete product response.");
    }

    private static ArenaProduct? TryParseProduct(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, isFinalBlock: false, state: default);
        string? name = null;
        int? major = null, minor = null, micro = null, revision = null;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            if (reader.ValueTextEquals("name") && reader.Read() && reader.TokenType == JsonTokenType.String)
                name = reader.GetString();
            else if (reader.ValueTextEquals("major") && reader.Read() && reader.TryGetInt32(out var majorValue))
                major = majorValue;
            else if (reader.ValueTextEquals("minor") && reader.Read() && reader.TryGetInt32(out var minorValue))
                minor = minorValue;
            else if (reader.ValueTextEquals("micro") && reader.Read() && reader.TryGetInt32(out var microValue))
                micro = microValue;
            else if (reader.ValueTextEquals("revision") && reader.Read() && reader.TryGetInt32(out var revisionValue))
                revision = revisionValue;

            if (name is not null && major.HasValue && minor.HasValue && micro.HasValue && revision.HasValue)
                return new ArenaProduct(name, major.Value, minor.Value, micro.Value, revision.Value);
        }
        return null;
    }

    public async Task<IReadOnlyList<ArenaSource>> GetNdiSourcesAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("sources", ct);
        await EnsureSuccessAsync(response, "read Arena sources", ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var sources = new List<ArenaSource>();
        if (document.RootElement.TryGetProperty("video", out var video))
        {
            foreach (var item in video.EnumerateArray())
            {
                var category = item.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "";
                if (!category.Equals("NDI Servers", StringComparison.OrdinalIgnoreCase)) continue;
                sources.Add(new ArenaSource(
                    item.TryGetProperty("idstring", out var id) ? id.GetString() ?? "" : "",
                    item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    category));
            }
        }
        return sources;
    }

    public async Task<string> GetCompositionNameAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "composition");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "read the Arena composition name", ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[64 * 1024];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), ct);
            if (read == 0) break;
            length += read;
            var name = TryParseCompositionName(buffer.AsSpan(0, length));
            if (name is not null) return name;
        }
        throw new InvalidDataException("Arena's composition response did not expose its name near the start of the document.");
    }

    private static string? TryParseCompositionName(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, isFinalBlock: false, state: default);
        var inName = false;
        var nameDepth = -1;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1
                && reader.ValueTextEquals("name"))
            {
                inName = true;
                nameDepth = reader.CurrentDepth;
                continue;
            }
            if (inName && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("value"))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    return reader.GetString() ?? "";
            }
            if (inName && reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == nameDepth)
                inName = false;
        }
        return null;
    }

    public Task NewCompositionAsync(CancellationToken ct) => PostAsync("composition/new", null, null, ct);

    public Task UpdateCompositionAsync(string name, int width, int height, CancellationToken ct) =>
        PutJsonAsync("composition", new { name = new { value = name }, video = new { width = new { value = width }, height = new { value = height } } }, ct);

    public Task GrowCompositionAsync(int columns, CancellationToken ct) =>
        PostJsonAsync("composition/grow-to", new { column_count = Math.Max(1, columns) }, ct);

    public async Task SaveCompositionAsync(string path, CancellationToken ct)
    {
        var saveStartedUtc = DateTime.UtcNow;
        using var saveCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var request = PostAsync("composition/save", ToFileUrl(path), "text/plain", saveCancellation.Token);
        DateTime observedWriteUtc = default;
        long observedLength = -1;
        var stableObservations = 0;

        // Arena 7.27 occasionally writes a complete AVC file but never finishes
        // the HTTP response. Treat a fully parseable on-disk composition as the
        // authoritative completion signal and cancel only the dangling request.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (request.IsCompleted)
            {
                await request;
                return;
            }
            if (IsCompleteCompositionFile(path) && File.GetLastWriteTimeUtc(path) >= saveStartedUtc)
            {
                var file = new FileInfo(path);
                if (file.LastWriteTimeUtc == observedWriteUtc && file.Length == observedLength)
                    stableObservations++;
                else
                {
                    observedWriteUtc = file.LastWriteTimeUtc;
                    observedLength = file.Length;
                    stableObservations = 1;
                }

                // Do not patch or reopen the AVC while Arena can still replace it.
                // Five unchanged observations provide a short but reliable settle window.
                if (stableObservations >= 5)
                {
                    saveCancellation.Cancel();
                    try { await request; }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    return;
                }
            }
            else stableObservations = 0;
            await Task.Delay(100, ct);
        }
        await request;
    }

    public async Task OpenCompositionAsync(string path, string expectedCompositionName, CancellationToken ct)
    {
        using var openCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var request = PostAsync("composition/open", ToFileUrl(path), "text/plain", openCancellation.Token);

        // Arena can apply the open immediately yet leave this response pending.
        // Give it time to load, then confirm the REST composition is responsive
        // and has the expected internal job name before releasing the request.
        var stableExpectedNameObservations = 0;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (request.IsCompleted)
            {
                await request;
                return;
            }
            await Task.Delay(100, ct);
            if (attempt < 7) continue;
            try
            {
                var state = await GetCompositionStateAsync(ct);
                if (!state.Name.Equals(expectedCompositionName, StringComparison.OrdinalIgnoreCase))
                {
                    stableExpectedNameObservations = 0;
                    continue;
                }

                // The composition name changes near the start of Arena's reload.
                // Let the complete graph settle before cancelling a dangling HTTP response.
                stableExpectedNameObservations++;
                if (stableExpectedNameObservations >= 10)
                {
                    openCancellation.Cancel();
                    try { await request; }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    return;
                }
            }
            catch (HttpRequestException) when (!ct.IsCancellationRequested)
            {
                stableExpectedNameObservations = 0;
            }
        }
        await request;
    }

    public Task AddLayerGroupAsync(CancellationToken ct) => PostAsync("composition/layergroups/add", null, null, ct);
    public Task AddLayerToGroupAsync(long groupId, CancellationToken ct) => PostAsync($"composition/layergroups/by-id/{groupId}/add-layer", null, null, ct);
    public Task DeleteLayerAsync(long layerId, CancellationToken ct) => DeleteAsync($"composition/layers/by-id/{layerId}", ct);
    public Task DeleteGroupAsync(long groupId, CancellationToken ct) => DeleteAsync($"composition/layergroups/by-id/{groupId}", ct);
    public Task DeleteColumnAsync(long columnId, CancellationToken ct) => DeleteAsync($"composition/columns/by-id/{columnId}", ct);

    public Task RenameGroupAsync(long groupId, string name, CancellationToken ct) =>
        PutJsonAsync($"composition/layergroups/by-id/{groupId}", new { name = new { value = name } }, ct);

    public Task RenameLayerAsync(long layerId, string name, CancellationToken ct) =>
        PutJsonAsync($"composition/layers/by-id/{layerId}", new { name = new { value = name } }, ct);

    public Task OpenSourceAsync(long clipId, string sourceName, CancellationToken ct)
    {
        // /insert adds a new grid column and invalidates the clip IDs that were
        // deliberately resolved after group creation. /open loads the source into
        // the existing slot, preserving both the grid and every final route index.
        return PostAsync($"composition/clips/by-id/{clipId}/open", BuildVideoSourceUrl(sourceName), "text/plain", ct);
    }

    public static string BuildVideoSourceUrl(string sourceName)
    {
        // Arena 7.27 documents '+' as valid for spaces but its source resolver does
        // not decode '+' in this path. Keep RFC 3986 %20 encoding.
        return $"source:///video/{Uri.EscapeDataString(sourceName)}";
    }

    public Task ConfigureVideoRouterFitAsync(long clipId, CancellationToken ct) => ConfigureClipFitAsync(clipId, "Video Router", ct);

    public Task ConfigureClipFitAsync(long clipId, CancellationToken ct) => ConfigureClipFitAsync(clipId, "clip", ct);

    public async Task UpdateClipThumbnailAsync(long clipId, CancellationToken ct)
    {
        var previous = await GetThumbnailStateAsync(clipId, ct);
        using var updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var request = PostAsync($"composition/clips/by-id/{clipId}/thumbnail/update", null, null, updateCancellation.Token);
        var responseCompleted = false;

        // Arena can capture the thumbnail while leaving the POST response pending.
        // A changed last_update token is ideal. Arena 7.27 can also keep that token
        // unchanged when it refreshes an already-live NDI thumbnail, so a stable,
        // non-default image after the capture has had time to run is also success.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (!responseCompleted && request.IsCompleted)
            {
                await request;
                responseCompleted = true;
            }
            await Task.Delay(50, ct);
            var current = await GetThumbnailStateAsync(clipId, ct);
            var tokenChanged = !string.IsNullOrEmpty(current.LastUpdate) && current.LastUpdate != previous.LastUpdate;
            var refreshedExistingImage = attempt >= 4 && !current.IsDefault;
            if (!tokenChanged && !refreshedExistingImage) continue;

            if (!responseCompleted)
            {
                updateCancellation.Cancel();
                try { await request; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            return;
        }

        if (!responseCompleted)
        {
            updateCancellation.Cancel();
            try { await request; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        throw new InvalidOperationException("Arena did not produce a live thumbnail for the NDI clip.");
    }

    public async Task ConnectClipAsync(long clipId, CancellationToken ct)
    {
        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var request = PostAsync($"composition/clips/by-id/{clipId}/connect", null, null, connectCancellation.Token);
        var responseCompleted = false;

        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (!responseCompleted && request.IsCompleted)
            {
                await request;
                responseCompleted = true;
            }

            using var document = await GetJsonAsync($"composition/clips/by-id/{clipId}", ct);
            if (document.RootElement.TryGetProperty("connected", out var connected) &&
                connected.ValueKind == JsonValueKind.Object &&
                connected.TryGetProperty("value", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString()?.StartsWith("Connected", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (!responseCompleted)
                {
                    connectCancellation.Cancel();
                    try { await request; }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
                return;
            }
            await Task.Delay(50, ct);
        }

        if (!responseCompleted) await request;
        throw new InvalidOperationException("Arena did not connect the Video Router clip.");
    }

    private async Task ConfigureClipFitAsync(long clipId, string clipDescription, CancellationToken ct)
    {
        JsonDocument? document = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            document?.Dispose();
            document = await GetJsonAsync($"composition/clips/by-id/{clipId}", ct);
            if (document.RootElement.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object) break;
            await Task.Delay(150, ct);
        }

        var loadedDocument = document ?? throw new InvalidOperationException($"Arena did not return the {clipDescription} state.");
        using (loadedDocument)
        {
            var resize = FindNamedParameter(loadedDocument.RootElement, "resize")
                ?? throw new InvalidOperationException($"Arena loaded the {clipDescription} but did not expose its Resize parameter.");
            await SetParameterValueAsync(resize.Id, "Fit", ct);

            for (var attempt = 0; attempt < 20; attempt++)
            {
                using var parameter = await GetJsonAsync($"parameter/by-id/{resize.Id}", ct);
                if (parameter.RootElement.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    value.GetString()?.Equals("Fit", StringComparison.OrdinalIgnoreCase) == true)
                    return;
                await Task.Delay(50, ct);
            }
            throw new InvalidOperationException($"Arena did not retain Resize = Fit for the {clipDescription}.");
        }
    }

    private async Task<ThumbnailState> GetThumbnailStateAsync(long clipId, CancellationToken ct)
    {
        using var document = await GetJsonAsync($"composition/clips/by-id/{clipId}", ct);
        if (!document.RootElement.TryGetProperty("thumbnail", out var thumbnail) ||
            thumbnail.ValueKind != JsonValueKind.Object) return new ThumbnailState(null, true);
        var update = thumbnail.TryGetProperty("last_update", out var updateValue) ? updateValue.ToString() : null;
        var isDefault = !thumbnail.TryGetProperty("is_default", out var defaultValue) || defaultValue.GetBoolean();
        return new ThumbnailState(update, isDefault);
    }

    private sealed record ThumbnailState(string? LastUpdate, bool IsDefault);

    public async Task<ArenaCompositionState> GetCompositionStateAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("composition", ct);
        await EnsureSuccessAsync(response, "read the Arena composition", ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        var layers = ParseLayers(root.GetProperty("layers"));
        var groups = new List<ArenaGroupState>();
        if (root.TryGetProperty("layergroups", out var groupItems))
        {
            foreach (var group in groupItems.EnumerateArray())
            {
                groups.Add(new ArenaGroupState(
                    group.GetProperty("id").GetInt64(),
                    ParameterValue(group, "name"),
                    ParseLayers(group.GetProperty("layers"))));
            }
        }
        var columns = root.TryGetProperty("columns", out var columnItems)
            ? columnItems.EnumerateArray().Select(c => c.GetProperty("id").GetInt64()).ToArray()
            : [];
        return new ArenaCompositionState(ParameterValue(root, "name"), layers, groups, columns);
    }

    private static IReadOnlyList<ArenaLayerState> ParseLayers(JsonElement items)
    {
        var layers = new List<ArenaLayerState>();
        foreach (var layer in items.EnumerateArray())
        {
            var clips = new List<long>();
            if (layer.TryGetProperty("clips", out var clipItems))
                clips.AddRange(clipItems.EnumerateArray().Select(c => c.GetProperty("id").GetInt64()));
            layers.Add(new ArenaLayerState(layer.GetProperty("id").GetInt64(), ParameterValue(layer, "name"), clips));
        }
        return layers;
    }

    private static string ParameterValue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var parameter) && parameter.TryGetProperty("value", out var value)
            ? value.GetString() ?? "" : "";

    private async Task<JsonDocument> GetJsonAsync(string uri, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, ct);
        await EnsureSuccessAsync(response, $"read {uri}", ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private Task SetParameterValueAsync<T>(long parameterId, T value, CancellationToken ct) =>
        PutJsonAsync($"parameter/by-id/{parameterId}", new { value }, ct);

    private static (long Id, string Name)? FindNamedParameter(JsonElement element, string requiredName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name.Replace("_", "", StringComparison.Ordinal), requiredName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("id", out var id) && id.TryGetInt64(out var parameterId))
                    return (parameterId, property.Name);
                var found = FindNamedParameter(property.Value, requiredName);
                if (found is not null) return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var found = FindNamedParameter(child, requiredName);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private async Task PutJsonAsync<T>(string uri, T value, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync(uri, value, JsonOptions, ct);
        await EnsureSuccessAsync(response, $"update {uri}", ct);
    }

    private async Task PostJsonAsync<T>(string uri, T value, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(uri, value, JsonOptions, ct);
        await EnsureSuccessAsync(response, $"update {uri}", ct);
    }

    private async Task PostAsync(string uri, string? body, string? contentType, CancellationToken ct)
    {
        using var content = body is null ? null : new StringContent(body, Encoding.UTF8, contentType ?? "text/plain");
        using var response = await _http.PostAsync(uri, content, ct);
        await EnsureSuccessAsync(response, $"update {uri}", ct);
    }

    private async Task DeleteAsync(string uri, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync(uri, ct);
        await EnsureSuccessAsync(response, $"delete {uri}", ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Arena could not {operation}: {(int)response.StatusCode} {response.ReasonPhrase}{(string.IsNullOrWhiteSpace(body) ? "" : $" — {body}")}", null, response.StatusCode);
    }

    public static string ToFileUrl(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private static bool IsCompleteCompositionFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0) return false;
            return XDocument.Load(stream).Root?.Name.LocalName == "Composition";
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.Xml.XmlException) { return false; }
    }

    public void Dispose() => _http.Dispose();
}

public sealed record ArenaCompositionState(string Name, IReadOnlyList<ArenaLayerState> Layers, IReadOnlyList<ArenaGroupState> Groups, IReadOnlyList<long> ColumnIds);
public sealed record ArenaLayerState(long Id, string Name, IReadOnlyList<long> ClipIds);
public sealed record ArenaGroupState(long Id, string Name, IReadOnlyList<ArenaLayerState> Layers);
