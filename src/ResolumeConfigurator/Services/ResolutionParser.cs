using System.Text.RegularExpressions;

namespace ResolumeConfigurator.Services;

public static partial class ResolutionParser
{
    [GeneratedRegex(@"(?<!\d)(?<width>\d{3,5})\s*[x×]\s*(?<height>\d{3,5})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DimensionsRegex();

    public static bool TryParse(string? value, out int width, out int height)
    {
        width = height = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = DimensionsRegex().Match(value);
        return match.Success
            && int.TryParse(match.Groups["width"].Value, out width)
            && int.TryParse(match.Groups["height"].Value, out height)
            && width is >= 320 and <= 32768
            && height is >= 240 and <= 32768;
    }
}
