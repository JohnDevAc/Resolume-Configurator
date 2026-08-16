using System.Globalization;
using System.Xml.Linq;

namespace ResolumeConfigurator.Services;

public sealed class SimpleOutputConfigurationService
{
    public async Task ApplyNdiCompositionSharingAsync(string preferenceFile, bool enabled, int width, int height, CancellationToken ct)
    {
        var document = File.Exists(preferenceFile)
            ? XDocument.Load(preferenceFile, LoadOptions.PreserveWhitespace)
            : new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("SimpleSetup", new XAttribute("advancedModeEnabled", "1")));
        var root = document.Root;
        if (root?.Name.LocalName != "SimpleSetup")
            throw new InvalidDataException("Resolume's SimpleOutput.xml does not contain a SimpleSetup root.");

        var outputs = root.Element("Outputs");
        if (outputs is null)
        {
            outputs = new XElement("Outputs");
            root.Add(outputs);
        }

        // Output > Composition output sharing > Network streaming (NDI) is
        // persisted as the composition-level NDI output device in SimpleOutput.xml.
        // It is separate from AdvancedOutput.xml and its decoder screen devices.
        foreach (var existing in outputs.Elements("OutputDeviceNDI").ToArray()) existing.Remove();
        if (enabled)
        {
            outputs.Add(new XElement("OutputDeviceNDI",
                new XAttribute("name", "Composition"),
                new XAttribute("deviceId", "NDIComposition"),
                new XAttribute("idHash", StableHash64("NDI|Composition")),
                new XAttribute("width", width),
                new XAttribute("height", height)));
        }

        var directory = Path.GetDirectoryName(preferenceFile)
            ?? throw new InvalidOperationException("Arena's Preferences directory could not be determined.");
        Directory.CreateDirectory(directory);
        if (File.Exists(preferenceFile))
        {
            var backup = preferenceFile + ".before-configurator-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff") + ".bak";
            File.Copy(preferenceFile, backup, false);
        }

        var temporary = Path.Combine(directory, $"SimpleOutput.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                await document.SaveAsync(stream, SaveOptions.None, ct);
            File.Move(temporary, preferenceFile, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        if (!PreferenceMatches(preferenceFile, enabled))
            throw new IOException("Arena's NDI composition-sharing preference could not be verified after writing it.");
    }

    public static bool PreferenceMatches(string preferenceFile, bool expectedEnabled)
    {
        try
        {
            var document = XDocument.Load(preferenceFile);
            var enabled = document.Root?.Element("Outputs")?.Elements("OutputDeviceNDI").Any() == true;
            return enabled == expectedEnabled;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.Xml.XmlException) { return false; }
    }

    private static string StableHash64(string value)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }
        return (hash == 0 ? 1UL : hash).ToString(CultureInfo.InvariantCulture);
    }
}
