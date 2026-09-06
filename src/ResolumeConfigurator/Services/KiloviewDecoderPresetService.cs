using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public sealed class KiloviewDecoderPresetService
{
    private const int N6PresetCapacity = 10;

    public async Task ValidateConnectionsAsync(IReadOnlyList<DecoderRow> decoders, CancellationToken ct)
    {
        foreach (var decoder in decoders)
        {
            if (decoder.Device.Credentials is null) throw new InvalidOperationException($"Saved credentials are missing for {decoder.OutputName}.");
            try
            {
                var isN60 = decoder.Device.Family.Contains("N60", StringComparison.OrdinalIgnoreCase)
                    || decoder.Device.Model.Contains("N60", StringComparison.OrdinalIgnoreCase);
                using var client = isN60 ? await AuthorizeN60Async(decoder, ct) : await AuthorizeN6Async(decoder, ct);
                using var presets = await GetJsonAsync(client, isN60 ? "/api/codec/preset/get" : "/api/preview/get", "check decoder preset access", ct);
                if (isN60)
                    SelectN60Slot(Data(presets.RootElement).EnumerateArray().Select(item => new N60PresetSummary(
                        Number(item, "id"), String(item, "channel_name"), String(item, "name"), String(item, "color"), IsN60PresetEmpty(item))), decoder.OutputName, out _);
                else
                    SelectN6Slot(N6Positions(presets.RootElement).Select(item => new N6PresetSummary(Number(item, "id"), String(item, "stream_name"))), decoder.OutputName, out _);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException or InvalidOperationException or OperationCanceledException))
            {
                throw new InvalidOperationException($"{decoder.OutputName} ({decoder.IpAddress}) failed the decoder preflight: {ex.Message}", ex);
            }
        }
    }

    public async Task<IReadOnlyList<DecoderPresetResult>> ConfigureAsync(
        IReadOnlyList<DecoderRow> decoders,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var results = new List<DecoderPresetResult>();
        foreach (var decoder in decoders)
        {
            if (decoder.Device.Credentials is null)
                throw new InvalidOperationException($"NDI Job Configurator has no saved credentials for decoder {decoder.OutputName} ({decoder.IpAddress}).");

            var isN60 = decoder.Device.Family.Contains("N60", StringComparison.OrdinalIgnoreCase)
                || decoder.Device.Model.Contains("N60", StringComparison.OrdinalIgnoreCase);
            var result = isN60
                ? await ConfigureN60Async(decoder, ct)
                : await ConfigureN6Async(decoder, ct);
            results.Add(result);
            progress?.Report($"{decoder.OutputName}: {(result.ReusedExistingSlot ? "updated existing" : "added to")} {result.Family} preset slot {result.Slot} and activated it.");
        }
        return results;
    }

    public static int SelectN60Slot(IEnumerable<N60PresetSummary> presets, string outputName, out bool reused)
    {
        var rows = presets.Where(preset => preset.Id > 0 && string.IsNullOrWhiteSpace(preset.Color)).OrderBy(preset => preset.Id).ToArray();
        var existing = rows.FirstOrDefault(preset => MatchesOutput(preset.ChannelName, preset.Name, outputName));
        if (existing is not null)
        {
            reused = true;
            return existing.Id;
        }

        var empty = rows.FirstOrDefault(preset => preset.IsEmpty);
        if (empty is null) throw new InvalidOperationException("The decoder has no unpopulated NDI preset slot available.");
        reused = false;
        return empty.Id;
    }

    public static int SelectN6Slot(IEnumerable<N6PresetSummary> presets, string outputName, out bool reused)
    {
        var rows = presets.Where(preset => preset.Id > 0).ToArray();
        var existing = rows.FirstOrDefault(preset => MatchesOutput("", preset.StreamName, outputName));
        if (existing is not null)
        {
            reused = true;
            return existing.Id;
        }
        if (rows.Length >= N6PresetCapacity)
            throw new InvalidOperationException($"The N6 decoder has no unpopulated preset slot (capacity {N6PresetCapacity}).");
        // N6 firmware owns monotonically allocated position IDs. Supplying a
        // guessed empty ID is rejected; zero means request its append/next-empty
        // operation, then read back the ID that firmware actually assigned.
        reused = false;
        return 0;
    }

    public static bool IsSameN6Source(string existingId, string existingUrl, string discoveredId, string discoveredUrl)
    {
        if (!string.IsNullOrWhiteSpace(existingUrl) && !string.IsNullOrWhiteSpace(discoveredUrl))
            return existingUrl.Trim().Equals(discoveredUrl.Trim(), StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrWhiteSpace(existingId) && !string.IsNullOrWhiteSpace(discoveredId)
            && existingId.Trim().Equals(discoveredId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsActiveN6Source(string channel, string name, string currentUrl, string outputName, string sourceUrl)
    {
        if (!MatchesOutput(channel, name, outputName) || string.IsNullOrWhiteSpace(currentUrl)) return false;
        if (currentUrl.Equals(sourceUrl, StringComparison.OrdinalIgnoreCase)) return true;

        // N6 firmware can expose the selected NDI sender on a different listener
        // port from the discovery/preset URL. The sender name and host remain
        // stable, so verify both instead of requiring the transient port.
        return TryGetHost(currentUrl, out var currentHost)
            && TryGetHost(sourceUrl, out var sourceHost)
            && currentHost.Equals(sourceHost, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<N6DecoderDiagnostic> InspectN6Async(DecoderRow decoder, CancellationToken ct)
    {
        if (decoder.Device.Credentials is null)
            throw new InvalidOperationException($"NDI Job Configurator has no saved credentials for decoder {decoder.OutputName} ({decoder.IpAddress}).");

        using var client = await AuthorizeN6Async(decoder, ct);
        using var presetsDocument = await GetJsonAsync(client, "/api/preview/get", "read N6 presets", ct);
        var presets = N6Positions(presetsDocument.RootElement)
            .Select(element => new N6PresetSummary(Number(element, "id"), String(element, "stream_name"), String(element, "stream_url", String(element, "url"))))
            .OrderBy(preset => preset.Id)
            .ToArray();

        var currentName = "";
        var currentUrl = "";
        foreach (var path in new[] { "/api/decoder/current/get.json", "/api/decoderMode/current/get.json" })
        {
            try
            {
                using var current = await GetJsonAsync(client, path, "read N6 active output", ct);
                var data = Data(current.RootElement);
                currentName = String(data, "channel_name", String(data, "name"));
                currentUrl = String(data, "original_url", String(data, "url", String(data, "ip")));
                if (!string.IsNullOrWhiteSpace(currentName) || !string.IsNullOrWhiteSpace(currentUrl)) break;
            }
            catch (HttpRequestException) { }
        }

        using var discovery = await PostJsonAsync(client, "/api/source/groups/list", new { is_need_stream = true, show_template = false }, "read N6 discovered sources", ct);
        var discovered = N6Streams(discovery.RootElement)
            .Where(source => !String(source, "address").Equals(decoder.IpAddress, StringComparison.OrdinalIgnoreCase))
            .Where(source => OutputSourceScore(source, decoder.OutputName) > 0)
            .OrderByDescending(source => OutputSourceScore(source, decoder.OutputName))
            .ThenByDescending(source => Number(source, "listener_port"))
            .FirstOrDefault();

        return new N6DecoderDiagnostic(
            decoder.OutputName,
            presets,
            currentName,
            currentUrl,
            discovered.ValueKind == JsonValueKind.Object ? String(discovered, "name", String(discovered, "channel_name")) : "",
            discovered.ValueKind == JsonValueKind.Object ? String(discovered, "url") : "");
    }

    public async Task<N60DecoderDiagnostic> InspectN60Async(DecoderRow decoder, CancellationToken ct)
    {
        if (decoder.Device.Credentials is null) throw new InvalidOperationException($"Saved credentials are missing for {decoder.OutputName}.");
        using var client = await AuthorizeN60Async(decoder, ct);
        using var presets = await GetJsonAsync(client, "/api/codec/preset/get", "inspect N60 presets", ct);
        var rows = Data(presets.RootElement).EnumerateArray().Select(item => new N60PresetSummary(
            Number(item, "id"), String(item, "channel_name"), String(item, "name"), String(item, "color"),
            IsN60PresetEmpty(item), String(item, "original_url", String(item, "url")))).ToArray();
        using var current = await GetJsonAsync(client, "/api/codec/decode/get", "inspect N60 active output", ct);
        var data = Data(current.RootElement);
        return new N60DecoderDiagnostic(decoder.OutputName, rows,
            String(data, "channel_name", String(data, "name")), String(data, "original_url", String(data, "url", String(data, "ip"))));
    }

    private static async Task<DecoderPresetResult> ConfigureN60Async(DecoderRow decoder, CancellationToken ct)
    {
        using var client = await AuthorizeN60Async(decoder, ct);
        using var presetsDocument = await GetJsonAsync(client, "/api/codec/preset/get", "read N60 presets", ct);
        var presets = Data(presetsDocument.RootElement).EnumerateArray().Select(element => new N60PresetSummary(
            Number(element, "id"),
            String(element, "channel_name"),
            String(element, "name"),
            String(element, "color"),
            IsN60PresetEmpty(element))).ToArray();
        var slot = SelectN60Slot(presets, decoder.OutputName, out var reused);
        var source = await FindN60SourceAsync(client, decoder.OutputName, ct);

        if (reused)
            using (await PostJsonAsync(client, "/api/codec/preset/remove", new { id = slot }, $"replace N60 preset {slot}", ct)) { }

        var sourceUrl = String(source, "original_url", String(source, "url"));
        using (await PostJsonAsync(client, "/api/codec/preset/add", new
        {
            position = slot,
            channel_name = String(source, "channel_name", decoder.OutputName),
            device_name = String(source, "device_name"),
            enable = 1,
            group = String(source, "group"),
            ip = String(source, "ip"),
            name = String(source, "name", decoder.OutputName),
            port = Number(source, "port", Number(source, "listener_port")),
            url = sourceUrl,
            original_url = sourceUrl,
            type = "ndi"
        }, $"add Arena output to N60 preset {slot}", ct)) { }

        using (await PostJsonAsync(client, "/api/codec/decode/add", new { id = slot }, $"activate N60 preset {slot}", ct, allowEmptyResult: true)) { }

        using var verifiedPresets = await GetJsonAsync(client, "/api/codec/preset/get", "verify N60 preset", ct);
        var retained = Data(verifiedPresets.RootElement).EnumerateArray().FirstOrDefault(item => Number(item, "id") == slot);
        if (retained.ValueKind != JsonValueKind.Object || !MatchesOutput(String(retained, "channel_name"), String(retained, "name"), decoder.OutputName))
            throw new InvalidOperationException($"{decoder.OutputName} did not retain its Arena NDI output in N60 preset slot {slot}.");
        await VerifyN60CurrentAsync(client, decoder.OutputName, sourceUrl, ct);
        return new DecoderPresetResult(decoder.OutputName, "N60", slot, reused);
    }

    private static async Task<DecoderPresetResult> ConfigureN6Async(DecoderRow decoder, CancellationToken ct)
    {
        using var client = await AuthorizeN6Async(decoder, ct);
        using var presetsDocument = await GetJsonAsync(client, "/api/preview/get", "read N6 presets", ct);
        var presetElements = N6Positions(presetsDocument.RootElement).Select(element => element.Clone()).ToArray();
        var presets = presetElements
            .Select(element => new N6PresetSummary(Number(element, "id"), String(element, "stream_name"), String(element, "stream_url", String(element, "url"))))
            .ToArray();
        var slot = SelectN6Slot(presets, decoder.OutputName, out var reused);
        var source = await FindN6SourceAsync(client, decoder.OutputName, decoder.IpAddress, ct);

        var streamId = String(source, "id");
        var streamName = String(source, "name", decoder.OutputName);
        var streamUrl = String(source, "url");
        var existingPreset = reused ? presetElements.First(element => Number(element, "id") == slot) : default;
        var alreadyCurrent = reused && IsSameN6Source(
            String(existingPreset, "stream_id", String(existingPreset, "id")),
            String(existingPreset, "stream_url", String(existingPreset, "url")),
            streamId,
            streamUrl);

        if (!alreadyCurrent)
        {
            object destination = reused
                ? new { type = "preview", stream_id = streamId, stream_name = streamName, stream_url = streamUrl, pos_id = slot }
                : new { type = "preview", stream_id = streamId, stream_name = streamName, stream_url = streamUrl };
            using (await PostJsonAsync(client, "/api/preview/source/modify", new
            {
                from = new { type = "source", stream_id = streamId, stream_name = streamName, stream_url = streamUrl, pos_id = "" },
                to = destination
            }, reused ? $"overwrite N6 preset {slot}" : "add Arena output to the next empty N6 preset", ct)) { }
        }

        var retainedSlot = slot;
        if (!alreadyCurrent)
        {
            using var verifiedPresets = await GetJsonAsync(client, "/api/preview/get", "verify N6 preset", ct);
            var retained = N6Positions(verifiedPresets.RootElement).FirstOrDefault(item => MatchesOutput(String(item, "stream_name"), "", decoder.OutputName));
            retainedSlot = Number(retained, "id");
            if (retained.ValueKind != JsonValueKind.Object || retainedSlot <= 0 || (reused && retainedSlot != slot))
                throw new InvalidOperationException(reused
                    ? $"{decoder.OutputName} was expected to remain in N6 preset slot {slot}, but the decoder reported slot {retainedSlot}."
                    : $"{decoder.OutputName} was not retained in a new N6 preset slot.");
        }

        try
        {
            using (await PostJsonAsync(client, "/api/decoder/current/set.json", new { name = streamName, url = streamUrl }, $"activate N6 preset {slot}", ct)) { }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("0301002", StringComparison.OrdinalIgnoreCase))
        {
            // This firmware rejects selecting the stream that is already active.
            // Treat only that exact no-op code as provisional success; the
            // current-output endpoint below remains the authoritative check.
        }
        await VerifyN6CurrentAsync(client, decoder.OutputName, streamUrl, ct);
        return new DecoderPresetResult(decoder.OutputName, "N6", retainedSlot, reused);
    }

    private static async Task<HttpClient> AuthorizeN60Async(DecoderRow decoder, CancellationToken ct)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        var client = new HttpClient(handler) { BaseAddress = new Uri($"http://{decoder.IpAddress}"), Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("App", "{\"language\":\"en\"}");
        try
        {
            using var login = await PostJsonAsync(client, "/api/systemctrl/users/login", new
            {
                username = decoder.Device.Credentials!.Username,
                password = decoder.Device.Credentials.Password
            }, "log in to N60 decoder", ct);
            var data = Data(login.RootElement);
            var token = String(data, "token");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", token);
            var uri = client.BaseAddress!;
            handler.CookieContainer.Add(uri, new Cookie("language", "en"));
            handler.CookieContainer.Add(uri, new Cookie("user", decoder.Device.Credentials.Username));
            handler.CookieContainer.Add(uri, new Cookie("alias", String(data, "alias", "Admin")));
            handler.CookieContainer.Add(uri, new Cookie("token", token));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<HttpClient> AuthorizeN6Async(DecoderRow decoder, CancellationToken ct)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        var client = new HttpClient(handler) { BaseAddress = new Uri($"http://{decoder.IpAddress}"), Timeout = TimeSpan.FromSeconds(45) };
        try
        {
            using var login = await PostJsonAsync(client, "/api/user/authorize.json", new
            {
                user = decoder.Device.Credentials!.Username,
                password = decoder.Device.Credentials.Password
            }, "log in to N6 decoder", ct);
            var data = Data(login.RootElement);
            var uri = client.BaseAddress!;
            handler.CookieContainer.Add(uri, new Cookie("username", decoder.Device.Credentials.Username));
            handler.CookieContainer.Add(uri, new Cookie("user", decoder.Device.Credentials.Username));
            handler.CookieContainer.Add(uri, new Cookie("alias", String(data, "alias", "Admin")));
            handler.CookieContainer.Add(uri, new Cookie("token", String(data, "token")));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<JsonElement> FindN60SourceAsync(HttpClient client, string outputName, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (attempt > 0) await Task.Delay(750, ct);
            using var discovery = await GetJsonAsync(client, "/api/codec/discovery/scan", "discover Arena NDI output on N60", ct);
            var match = Flatten(Data(discovery.RootElement))
                .Where(source => OutputSourceScore(source, outputName) > 0)
                .OrderByDescending(source => OutputSourceScore(source, outputName))
                .ThenByDescending(source => Number(source, "port", Number(source, "listener_port")))
                .FirstOrDefault();
            if (match.ValueKind == JsonValueKind.Object) return match.Clone();
        }
        throw new InvalidOperationException($"The N60 decoder could not discover Arena's NDI output '{outputName}'. Confirm the Advanced Output NDI screen is enabled.");
    }

    private static async Task<JsonElement> FindN6SourceAsync(HttpClient client, string outputName, string decoderIpAddress, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (attempt > 0) await Task.Delay(750, ct);
            using var discovery = await PostJsonAsync(client, "/api/source/groups/list", new { is_need_stream = true, show_template = false }, "discover Arena NDI output on N6", ct);
            var match = N6Streams(discovery.RootElement)
                // Every N6 decoder advertises its own "Decoding Channel" using
                // the decoder hostname. It is not the similarly named Arena AO
                // sender and must never win the output-name match.
                .Where(source => !String(source, "address").Equals(decoderIpAddress, StringComparison.OrdinalIgnoreCase))
                .Where(source => OutputSourceScore(source, outputName) > 0)
                .OrderByDescending(source => OutputSourceScore(source, outputName))
                .ThenByDescending(source => Number(source, "listener_port"))
                .FirstOrDefault();
            if (match.ValueKind == JsonValueKind.Object) return match.Clone();
        }
        throw new InvalidOperationException($"The N6 decoder could not discover Arena's NDI output '{outputName}'. Confirm the Advanced Output NDI screen is enabled.");
    }

    private static async Task VerifyN60CurrentAsync(HttpClient client, string outputName, string sourceUrl, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (attempt > 0) await Task.Delay(500, ct);
            using var current = await GetJsonAsync(client, "/api/codec/decode/get", "verify N60 active output", ct);
            var data = Data(current.RootElement);
            if (MatchesOutput(String(data, "channel_name"), String(data, "name"), outputName)
                || SameUrl(data, sourceUrl)) return;
        }
        throw new InvalidOperationException($"The N60 decoder did not activate Arena output '{outputName}'.");
    }

    private static async Task VerifyN6CurrentAsync(HttpClient client, string outputName, string sourceUrl, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (attempt > 0) await Task.Delay(500, ct);
            foreach (var path in new[] { "/api/decoder/current/get.json", "/api/decoderMode/current/get.json" })
            {
                try
                {
                    using var current = await GetJsonAsync(client, path, "verify N6 active output", ct);
                    var data = Data(current.RootElement);
                    var currentUrl = String(data, "original_url", String(data, "url", String(data, "ip")));
                    if (IsActiveN6Source(
                        String(data, "channel_name"),
                        String(data, "name"),
                        currentUrl,
                        outputName,
                        sourceUrl)) return;
                }
                catch (HttpRequestException) { }
            }
        }
        throw new InvalidOperationException($"The N6 decoder did not activate Arena output '{outputName}'.");
    }

    private static bool SameUrl(JsonElement data, string sourceUrl)
    {
        var value = String(data, "original_url", String(data, "url", String(data, "ip")));
        return !string.IsNullOrWhiteSpace(value) && value.Equals(sourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    private static int OutputSourceScore(JsonElement source, string outputName)
    {
        var channel = String(source, "channel_name").Trim();
        var name = String(source, "name", String(source, "ndi_name")).Trim();
        if (channel.Equals(outputName, StringComparison.OrdinalIgnoreCase)) return 20;
        if (name.Equals(outputName, StringComparison.OrdinalIgnoreCase)) return 18;
        if (MatchesOutputName(name, outputName)) return 12;
        if (MatchesOutputName(channel, outputName)) return 10;
        return 0;
    }

    private static bool MatchesOutput(string channel, string name, string outputName) =>
        MatchesOutputName(channel, outputName) || MatchesOutputName(name, outputName);

    private static bool MatchesOutputName(string value, string outputName)
    {
        if (string.IsNullOrWhiteSpace(outputName)) return false;
        value = value.Trim();
        // NDI uses "HOST (channel)"; Arena may prefix its channel with "Arena - ".
        // Substring matching would reuse KV-0010 when configuring KV-001.
        return value.Equals(outputName, StringComparison.OrdinalIgnoreCase)
            || value.Equals($"Arena - {outputName}", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith($" ({outputName})", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith($" (Arena - {outputName})", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetHost(string value, out string host)
    {
        var candidate = value.Contains("://", StringComparison.Ordinal) ? value : $"ndi://{value}";
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            host = uri.Host;
            return true;
        }
        host = "";
        return false;
    }

    private static bool IsN60PresetEmpty(JsonElement preset) =>
        string.IsNullOrWhiteSpace(String(preset, "channel_name"))
        && string.IsNullOrWhiteSpace(String(preset, "name"))
        && string.IsNullOrWhiteSpace(String(preset, "url"))
        && string.IsNullOrWhiteSpace(String(preset, "original_url"))
        && string.IsNullOrWhiteSpace(String(preset, "ip"));

    private static IEnumerable<JsonElement> Flatten(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                foreach (var nested in Flatten(item)) yield return nested;
            yield break;
        }
        if (value.ValueKind != JsonValueKind.Object) yield break;
        yield return value;
        if (value.TryGetProperty("children", out var children))
            foreach (var nested in Flatten(children)) yield return nested;
        if (value.TryGetProperty("streams", out var streams))
            foreach (var nested in Flatten(streams)) yield return nested;
    }

    private static IEnumerable<JsonElement> N6Streams(JsonElement root)
    {
        var groups = Data(root);
        if (groups.ValueKind != JsonValueKind.Array) yield break;
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array) continue;
            foreach (var stream in streams.EnumerateArray()) if (stream.ValueKind == JsonValueKind.Object) yield return stream;
        }
    }

    private static IEnumerable<JsonElement> N6Positions(JsonElement root)
    {
        var data = Data(root);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("position", out var positions) || positions.ValueKind != JsonValueKind.Array) yield break;
        foreach (var position in positions.EnumerateArray()) if (position.ValueKind == JsonValueKind.Object) yield return position;
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path, string operation, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct);
        return await ReadJsonAsync(response, operation, ct);
    }

    private static async Task<JsonDocument> PostJsonAsync(HttpClient client, string path, object body, string operation, CancellationToken ct, bool allowEmptyResult = false)
    {
        using var response = await client.PostAsJsonAsync(path, body, ct);
        return await ReadJsonAsync(response, operation, ct, allowEmptyResult);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string operation, CancellationToken ct, bool allowEmptyResult = false)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{operation} failed with HTTP {(int)response.StatusCode}.");
        JsonDocument document;
        try { document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); }
        catch (JsonException ex) { throw new InvalidDataException($"{operation} returned invalid JSON.", ex); }

        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.String)
        {
            var value = result.GetString() ?? "";
            var success = value.Equals("ok", StringComparison.OrdinalIgnoreCase)
                || value.Equals("success", StringComparison.OrdinalIgnoreCase)
                || (allowEmptyResult && string.IsNullOrWhiteSpace(value));
            if (!success && !string.IsNullOrWhiteSpace(value))
            {
                var message = document.RootElement.TryGetProperty("msg", out var msg) ? msg.ToString() : value;
                document.Dispose();
                throw new InvalidOperationException($"{operation} was rejected: {message}");
            }
        }
        return document;
    }

    private static JsonElement Data(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;

    private static string String(JsonElement element, string name, string fallback = "")
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? fallback : value.ToString();
    }

    private static int Number(JsonElement element, string name, int fallback = 0)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : int.TryParse(value.ToString(), out number) ? number : fallback;
    }
}

public sealed record N60PresetSummary(int Id, string ChannelName, string Name, string Color, bool IsEmpty, string SourceUrl = "");
public sealed record N60DecoderDiagnostic(string DecoderName, IReadOnlyList<N60PresetSummary> Presets, string CurrentName, string CurrentUrl);
public sealed record N6PresetSummary(int Id, string StreamName, string StreamUrl = "");
public sealed record N6DecoderDiagnostic(
    string DecoderName,
    IReadOnlyList<N6PresetSummary> Presets,
    string CurrentName,
    string CurrentUrl,
    string DiscoveredName,
    string DiscoveredUrl);
