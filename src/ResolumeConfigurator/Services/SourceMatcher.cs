using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public static class SourceMatcher
{
    private sealed record Identity(string Host, string Channel);

    public static ArenaSource? BestMatch(JobDevice device, IEnumerable<ArenaSource> sources) => CreateMatcher(sources)(device);

    public static Func<JobDevice, ArenaSource?> CreateMatcher(IEnumerable<ArenaSource> sources)
    {
        var candidates = sources.Select(source => (Source: source, Name: Parse(source.Name), Token: Parse(source.IdString))).ToArray();
        return device =>
        {
            var host = Normalize(device.Hostname);
            var channel = Normalize(device.NdiChannelName);
            if (Parse(channel) is { } advertised && (host.Length == 0 || Same(host, advertised.Host)))
            {
                host = advertised.Host;
                channel = advertised.Channel;
            }
            if (host.Length == 0 && channel.Length == 0) return null;
            ArenaSource? match = null;
            foreach (var candidate in candidates)
            {
                // The display name and token cannot supply contradictory pairs.
                if (candidate.Name is { } name && candidate.Token is { } token
                    && (!Same(name.Host, token.Host) || !Same(name.Channel, token.Channel))) continue;
                var identity = candidate.Name ?? candidate.Token;
                var matches = identity is not null
                    ? (host.Length == 0 || Same(host, identity.Host)) && (channel.Length == 0 || Same(channel, identity.Channel))
                    : (host.Length == 0 || channel.Length == 0)
                        && new[] { candidate.Source.Name, candidate.Source.IdString }.Any(value => Same(value.Trim(), host.Length > 0 ? host : channel));
                if (!matches) continue;
                if (match is not null && match != candidate.Source) return null;
                match = candidate.Source;
            }
            return match;
        };
    }

    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string value) => value.Any(char.IsLetterOrDigit) ? value.Trim() : "";
    private static Identity? Parse(string value)
    {
        value = value.Trim();
        // Machine names precede the first separator; the channel may contain parentheses.
        var separator = value.IndexOf(" (", StringComparison.Ordinal);
        return separator > 0 && value.EndsWith(')') && separator + 2 < value.Length - 1
            ? new(value[..separator], value[(separator + 2)..^1]) : null;
    }
}
