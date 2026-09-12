using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public sealed class ResolumeApiClient : IPostRestartArenaApi
{
    private readonly HttpClient _http;
    private readonly TimeSpan _completionTimeout;
    public Func<CancellationToken, Task>? BeforeMutation { get; set; }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public ResolumeApiClient(string baseAddress = "http://127.0.0.1:8080/api/v1/", TimeSpan? timeout = null, HttpMessageHandler? handler = null, TimeSpan? completionTimeout = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(baseAddress);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(60);
        _completionTimeout = completionTimeout ?? TimeSpan.FromSeconds(60);
        // Arena is deliberately killed and relaunched during one app workflow.
        // Do not retain keep-alive sockets belonging to the previous process;
        // a stale accepted connection can otherwise outlive the old listener
        // while the replacement webserver is already healthy.
        _http.DefaultRequestHeaders.ConnectionClose = true;
    }

    public async Task<ArenaProduct> GetProductAsync(CancellationToken ct = default)
    {
        using var timeout = CreateStreamingTimeout(ct);
        ct = timeout.Token;
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
        using var timeout = CreateStreamingTimeout(ct);
        ct = timeout.Token;
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
        await ValidateMutationAsync(ct);
        ct.ThrowIfCancellationRequested();
        // Absence at dispatch establishes fresh-save evidence even on filesystems
        // with coarse/skewed timestamps or when Arena saves identical XML bytes.
        string? previous = null;
        if (File.Exists(path))
        {
            previous = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".configurator-save-{Guid.NewGuid():N}.previous");
            File.Move(path, previous, false);
        }
        (DateTime WriteUtc, long Length) observed = (default, -1);
        var stable = 0;
        try
        {
            await VerifiedPostAsync("composition/save", ToFileUrl(path), "text/plain", (_, _) =>
            {
                var current = FileStamp(path);
                if (current.Length > 0)
                {
                    stable = current == observed ? stable + 1 : 1;
                    observed = current;
                    return Task.FromResult(stable >= 5 && IsCompleteCompositionFile(path));
                }
                stable = 0;
                return Task.FromResult(false);
            }, ct);
        }
        catch (Exception ex)
        {
            if (previous is not null)
            {
                if (!File.Exists(path))
                {
                    try { File.Move(previous, path, false); }
                    catch (IOException) { /* A concurrent writer may have recreated the target; retain the prior bytes. */ }
                }
                if (File.Exists(previous))
                {
                    var message = $"Arena save was not verified. The prior composition is retained at {previous}. {ex.Message}";
                    if (ex is OperationCanceledException) throw new OperationCanceledException(message, ex, ct);
                    throw new IOException(message, ex);
                }
            }
            throw;
        }
        // The verified file is authoritative; a locked recovery copy may safely remain.
        if (previous is not null)
            try { File.Delete(previous); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public async Task OpenCompositionAsync(string path, string expectedCompositionName, CancellationToken ct)
    {
        var saved = XDocument.Load(path);
        if (saved.Root?.Name.LocalName != "Composition") throw new InvalidDataException("The saved file is not an Arena composition.");
        await ValidateMutationAsync(ct);
        var stable = 0;
        await VerifiedPostAsync("composition/open", ToFileUrl(path), "text/plain", async (_, token) =>
        {
            try
            {
                var state = await GetCompositionStateAsync(token);
                stable = state.Name.Equals(expectedCompositionName, StringComparison.OrdinalIgnoreCase)
                    && ArenaCompositionSynchronizationService.MatchesSavedStructure(saved, state) ? stable + 1 : 0;
                return stable >= 5;
            }
            catch (HttpRequestException) when (!token.IsCancellationRequested)
            {
                stable = 0;
                return false;
            }
        }, ct);
    }

    public Task AddLayerGroupAsync(CancellationToken ct) => PostAsync("composition/layergroups/add", null, null, ct);
    public Task AddLayerToGroupAsync(long groupId, CancellationToken ct) => PostAsync($"composition/layergroups/by-id/{groupId}/add-layer", null, null, ct);
    public Task MoveLayerToGroupAsync(long groupId, int layerIndex, CancellationToken ct) =>
        PostAsync($"composition/layergroups/by-id/{groupId}/move-layer", $"/composition/layers/{layerIndex}", "text/plain", ct);
    public Task ClearLayerClipsAsync(long layerId, CancellationToken ct) =>
        PostAsync($"composition/layers/by-id/{layerId}/clearclips", null, null, ct);
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
        await ValidateMutationAsync(ct);
        await VerifiedPostAsync($"composition/clips/by-id/{clipId}/thumbnail/update", null, null, async (acknowledged, token) =>
        {
            var current = await GetThumbnailStateAsync(clipId, token);
            var tokenChanged = !string.IsNullOrEmpty(current.LastUpdate) && current.LastUpdate != previous.LastUpdate;
            return !current.IsDefault && (tokenChanged || previous.IsDefault || acknowledged);
        }, ct);
    }

    public async Task ConnectClipAsync(long clipId, CancellationToken ct)
    {
        await ValidateMutationAsync(ct);
        await VerifiedPostAsync($"composition/clips/by-id/{clipId}/connect", null, null, async (_, token) =>
        {
            using var document = await GetJsonAsync($"composition/clips/by-id/{clipId}", token);
            return document.RootElement.TryGetProperty("connected", out var connected)
                && connected.ValueKind == JsonValueKind.Object
                && connected.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString()?.StartsWith("Connected", StringComparison.OrdinalIgnoreCase) == true;
        }, ct);
    }

    private Task ValidateMutationAsync(CancellationToken ct) => BeforeMutation?.Invoke(ct) ?? Task.CompletedTask;

    // Called only after validation. This request represents HTTP dispatch, so
    // completion detection can never cancel or suppress the mutation guard.
    private async Task VerifiedPostAsync(string uri, string? body, string? contentType,
        Func<bool, CancellationToken, Task<bool>> completed, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_completionTimeout);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var request = PostCoreAsync(uri, body, contentType, requestCancellation.Token);
        try
        {
            while (true)
            {
                if (request.IsCompleted) await request;
                if (await completed(request.IsCompletedSuccessfully, deadline.Token))
                {
                    requestCancellation.Cancel();
                    try { await request; }
                    catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                    return;
                }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Arena could not verify {uri}: the HTTP request or {_completionTimeout.TotalSeconds:0.#}-second completion deadline expired. Check the saved file/live state before retrying.");
        }
        finally
        {
            requestCancellation.Cancel();
            try { await request; } catch { /* Observe cleanup without replacing the original failure. */ }
        }
    }

    private static (DateTime WriteUtc, long Length) FileStamp(string path)
    {
        try { var file = new FileInfo(path); return file.Exists ? (file.LastWriteTimeUtc, file.Length) : (default, -1); }
        catch (IOException) { return (default, -1); }
        catch (UnauthorizedAccessException) { return (default, -1); }
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
        if (BeforeMutation is not null) await BeforeMutation(ct);
        using var response = await _http.PutAsJsonAsync(uri, value, JsonOptions, ct);
        await EnsureSuccessAsync(response, $"update {uri}", ct);
    }

    private async Task PostJsonAsync<T>(string uri, T value, CancellationToken ct)
    {
        if (BeforeMutation is not null) await BeforeMutation(ct);
        using var response = await _http.PostAsJsonAsync(uri, value, JsonOptions, ct);
        await EnsureSuccessAsync(response, $"update {uri}", ct);
    }

    private async Task PostAsync(string uri, string? body, string? contentType, CancellationToken ct)
    {
        await ValidateMutationAsync(ct);
        await PostCoreAsync(uri, body, contentType, ct);
    }

    private async Task PostCoreAsync(string uri, string? body, string? contentType, CancellationToken ct)
    {
        using var content = body is null ? null : new StringContent(body, Encoding.UTF8, contentType ?? "text/plain");
        using var response = await _http.PostAsync(uri, content, ct);
        await EnsureSuccessAsync(response, $"update {uri}", ct);
    }

    private async Task DeleteAsync(string uri, CancellationToken ct)
    {
        if (BeforeMutation is not null) await BeforeMutation(ct);
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

    private CancellationTokenSource CreateStreamingTimeout(CancellationToken ct)
    {
        // ResponseHeadersRead ends HttpClient's timeout at the headers. Keep a
        // deadline alive through body reads, including unsuccessful responses.
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_http.Timeout);
        return timeout;
    }

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
