using System.Text.RegularExpressions;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public static partial class SourceMatcher
{
    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumeric();

    public static ArenaSource? BestMatch(JobDevice device, IEnumerable<ArenaSource> sources)
    {
        var identities = new[] { device.NdiChannelName, device.Hostname }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(value => (Value: value, Normalized: Normalize(value)))
            .Where(identity => identity.Normalized.Length > 0)
            .ToArray();
        return sources.Select(source => new { Source = source, Score = Score(source, identities) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Source.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Source)
            .FirstOrDefault();
    }

    private static int Score(ArenaSource source, IReadOnlyList<(string Value, string Normalized)> identities)
    {
        var sourceValues = new[] { source.Name, source.IdString };
        var best = 0;
        foreach (var sourceValue in sourceValues)
        {
            var normalizedSource = Normalize(sourceValue);
            foreach (var identity in identities)
            {
                if (sourceValue.Equals(identity.Value, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 100);
                if (normalizedSource == identity.Normalized) best = Math.Max(best, 95);
                else if (normalizedSource.StartsWith(identity.Normalized, StringComparison.Ordinal)) best = Math.Max(best, 80);
                else if (normalizedSource.Contains(identity.Normalized, StringComparison.Ordinal)) best = Math.Max(best, 60);
            }
        }
        return best;
    }

    private static string Normalize(string value) => NonAlphaNumeric().Replace(value.ToLowerInvariant(), "");
}
