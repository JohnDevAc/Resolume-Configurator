using System.Xml.Linq;

namespace ResolumeConfigurator.Services;

public sealed class AdvancedOutputActivator
{
    public async Task ActivateAsync(string presetFile, string preferenceFile, IReadOnlyCollection<string> expectedScreens, CancellationToken ct)
    {
        if (!File.Exists(presetFile)) throw new FileNotFoundException("The generated Advanced Output preset was not found.", presetFile);

        var preset = XDocument.Load(presetFile);
        var active = CreateActiveDocument(preset, expectedScreens);
        await AtomicFile.WriteXmlAsync(preferenceFile, active, ct, backup: true);
        if (!PreferenceMatches(preferenceFile, expectedScreens))
            throw new IOException("Arena's active Advanced Output XML could not be verified after writing it.");
    }

    internal static XDocument CreateActiveDocument(XDocument preset, IReadOnlyCollection<string> expectedScreens)
    {
        var setup = preset.Root?.Name.LocalName == "XmlState"
            ? preset.Root.Element("ScreenSetup")
            : preset.Root?.Name.LocalName == "ScreenSetup" ? preset.Root : null;
        if (setup is null) throw new InvalidDataException("The generated preset does not contain a ScreenSetup.");

        var activeSetup = new XElement(setup);
        var versionInfo = preset.Root?.Element("versionInfo");
        if (versionInfo is not null && activeSetup.Element("versionInfo") is null)
            activeSetup.AddFirst(new XElement(versionInfo));

        var actualScreens = activeSetup.Descendants("Screen")
            .Select(screen => (string?)screen.Attribute("name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (actualScreens.Count != expectedScreens.Count || !expectedScreens.All(actualScreens.Contains))
            throw new InvalidDataException("The active Advanced Output state does not contain the expected screens.");

        return new XDocument(new XDeclaration("1.0", "utf-8", null), activeSetup);
    }

    public static bool PreferenceMatches(string preferenceFile, IReadOnlyCollection<string> expectedScreens)
    {
        try
        {
            var document = XDocument.Load(preferenceFile);
            var actual = document.Descendants("Screen")
                .Select(screen => (string?)screen.Attribute("name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return actual.Count == expectedScreens.Count && expectedScreens.All(actual.Contains);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.Xml.XmlException) { return false; }
    }
}
