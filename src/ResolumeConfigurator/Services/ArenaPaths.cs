namespace ResolumeConfigurator.Services;

public sealed record ArenaUserPaths(string Root, string Compositions, string AdvancedOutputPresets, string AdvancedOutputPreference);

public static class ArenaPaths
{
    public static ArenaUserPaths Resolve()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Resolume Arena"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "Resolume Arena")
        }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var root = candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
        return new ArenaUserPaths(
            root,
            Path.Combine(root, "Compositions"),
            Path.Combine(root, "Presets", "Advanced Output"),
            Path.Combine(root, "Preferences", "AdvancedOutput.xml"));
    }

    public static string SafeFileName(string value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }
}
