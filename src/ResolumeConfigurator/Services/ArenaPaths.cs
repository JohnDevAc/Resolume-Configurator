using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ResolumeConfigurator.Services;

public sealed record ArenaUserPaths(
    string Root,
    string Compositions,
    string AdvancedOutputPresets,
    string AdvancedOutputPreference,
    string SimpleOutputPreference);

public static class ArenaPaths
{
    public static ArenaUserPaths Resolve()
    {
        var root = ResolveOverride("RESOLUME_ARENA_DATA_DIR");
        if (root is null)
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrWhiteSpace(documents)) throw new InvalidOperationException("Windows did not supply a Documents folder. Set RESOLUME_ARENA_DATA_DIR explicitly.");
            root = Path.Combine(documents, "Resolume Arena");
        }
        return FromRoot(root);
    }

    public static ArenaUserPaths FromRoot(string root)
    {
        root = Path.GetFullPath(root);
        return new ArenaUserPaths(
            root,
            Path.Combine(root, "Compositions"),
            Path.Combine(root, "Presets", "Advanced Output"),
            Path.Combine(root, "Preferences", "AdvancedOutput.xml"),
            Path.Combine(root, "Preferences", "SimpleOutput.xml"));
    }

    public static string SafeFileName(string value, string fallback, int maximumLength = 120)
    {
        if (maximumLength < 24) throw new InvalidOperationException("The Arena directory is too long. Select a shorter RESOLUME_ARENA_DATA_DIR.");
        var invalid = Path.GetInvalidFileNameChars();
        var original = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var cleaned = new string(original.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "NDI Job";
        if (Regex.IsMatch(cleaned.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            cleaned = "_" + cleaned;
        // Leave room for extensions and backup suffixes within one path component.
        if (cleaned != original || cleaned.Length > maximumLength)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original)))[..12].ToLowerInvariant();
            cleaned = cleaned[..Math.Min(cleaned.Length, maximumLength - 13)].TrimEnd();
            if (char.IsHighSurrogate(cleaned[^1])) cleaned = cleaned[..^1];
            cleaned += "-" + hash;
        }
        return cleaned;
    }

    public static string OutputPath(string directory, string name, string extension)
    {
        directory = Path.GetFullPath(directory);
        // Arena is a separate process; do not rely on this app's longPathAware manifest.
        var maximum = Math.Min(120, 240 - directory.Length - extension.Length - 1);
        return Path.Combine(directory, SafeFileName(name, "NDI Job", maximum) + extension);
    }

    public static string? ResolveOverride(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        try
        {
            if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("An absolute path is required.");
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            throw new InvalidOperationException($"{variable} must contain a valid absolute path.", ex);
        }
    }
}
