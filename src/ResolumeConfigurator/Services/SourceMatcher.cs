using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public static class SourceMatcher
{
    public static ArenaSource? BestMatch(JobDevice device, IEnumerable<ArenaSource> sources)
        => CreateMatcher(sources)(device);

    public static Func<JobDevice, ArenaSource?> CreateMatcher(IEnumerable<ArenaSource> sources)
    {
        var candidates = sources.Select(source => (Source: source,
            Values: new[] { source.Name, source.IdString }.SelectMany(IdentityParts).ToArray())).ToArray();
        return device => BestMatch(device, candidates);
    }

    private static ArenaSource? BestMatch(JobDevice device,
        IReadOnlyList<(ArenaSource Source, string[] Values)> candidates)
    {
        var identities = new[] { device.NdiChannelName, device.Hostname }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(value => value.Trim())
            .Where(identity => identity.Any(char.IsLetterOrDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ArenaSource? best = null;
        var bestScore = 0;
        var ambiguous = false;
        foreach (var candidate in candidates)
        {
            var score = Score(candidate.Values, identities);
            if (score > bestScore)
            {
                best = candidate.Source;
                bestScore = score;
                ambiguous = false;
            }
            else if (score > 0 && score == bestScore && candidate.Source != best) ambiguous = true;
        }
        return ambiguous ? null : best;
    }

    private static int Score(IReadOnlyList<string> sourceValues, IReadOnlyList<string> identities)
    {
        var total = 0;
        foreach (var identity in identities)
        {
            var best = 0;
            foreach (var sourceValue in sourceValues)
            {
                if (sourceValue.Equals(identity, StringComparison.OrdinalIgnoreCase)) best = 100;
            }
            total += best;
        }
        return total;
    }

    private static IEnumerable<string> IdentityParts(string value)
    {
        value = value.Trim();
        yield return value;
        // NDI source identities are HOST (channel). Match whole components,
        // never a numeric prefix such as TT-001 inside TT-0010.
        var separator = value.LastIndexOf(" (", StringComparison.Ordinal);
        if (separator > 0 && value.EndsWith(')'))
        {
            yield return value[..separator];
            yield return value[(separator + 2)..^1];
        }
    }
}
