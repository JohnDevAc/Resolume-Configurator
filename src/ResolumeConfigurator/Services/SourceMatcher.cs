using System.Text.RegularExpressions;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public static partial class SourceMatcher
{
    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumeric();

    public static ArenaSource? BestMatch(JobDevice device, IEnumerable<ArenaSource> sources)
        => CreateMatcher(sources)(device);

    public static Func<JobDevice, ArenaSource?> CreateMatcher(IEnumerable<ArenaSource> sources)
    {
        var candidates = sources.Select(source => (Source: source,
            Values: new[] { source.Name, source.IdString }.Select(value => (Value: value, Normalized: Normalize(value))).ToArray())).ToArray();
        return device => BestMatch(device, candidates);
    }

    private static ArenaSource? BestMatch(JobDevice device,
        IReadOnlyList<(ArenaSource Source, (string Value, string Normalized)[] Values)> candidates)
    {
        var identities = new[] { device.NdiChannelName, device.Hostname }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(value => (Value: value, Normalized: Normalize(value)))
            .Where(identity => identity.Normalized.Length > 0)
            .ToArray();
        ArenaSource? best = null;
        var bestScore = 0;
        foreach (var candidate in candidates)
        {
            var score = Score(candidate.Values, identities);
            if (score > bestScore || score > 0 && score == bestScore
                && StringComparer.OrdinalIgnoreCase.Compare(candidate.Source.Name, best?.Name) < 0)
            {
                best = candidate.Source;
                bestScore = score;
            }
        }
        return best;
    }

    private static int Score(IReadOnlyList<(string Value, string Normalized)> sourceValues, IReadOnlyList<(string Value, string Normalized)> identities)
    {
        var best = 0;
        foreach (var sourceValue in sourceValues)
        {
            var normalizedSource = sourceValue.Normalized;
            foreach (var identity in identities)
            {
                if (sourceValue.Value.Equals(identity.Value, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 100);
                if (normalizedSource == identity.Normalized) best = Math.Max(best, 95);
                else if (normalizedSource.StartsWith(identity.Normalized, StringComparison.Ordinal)) best = Math.Max(best, 80);
                else if (normalizedSource.Contains(identity.Normalized, StringComparison.Ordinal)) best = Math.Max(best, 60);
            }
        }
        return best;
    }

    private static string Normalize(string value) => NonAlphaNumeric().Replace(value.ToLowerInvariant(), "");
}
