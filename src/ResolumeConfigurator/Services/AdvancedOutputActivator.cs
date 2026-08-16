using System.Xml.Linq;

namespace ResolumeConfigurator.Services;

public sealed class AdvancedOutputActivator
{
    public async Task ActivateAsync(string presetFile, string preferenceFile, IReadOnlyCollection<string> expectedScreens, CancellationToken ct)
    {
        if (!File.Exists(presetFile)) throw new FileNotFoundException("The generated Advanced Output preset was not found.", presetFile);

        var preset = XDocument.Load(presetFile);
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

        var directory = Path.GetDirectoryName(preferenceFile)
            ?? throw new InvalidOperationException("Arena's Preferences directory could not be determined.");
        Directory.CreateDirectory(directory);
        if (File.Exists(preferenceFile))
        {
            var backup = preferenceFile + ".before-configurator-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
            File.Copy(preferenceFile, backup, false);
        }

        var temporary = Path.Combine(directory, $"AdvancedOutput.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                await new XDocument(new XDeclaration("1.0", "utf-8", null), activeSetup).SaveAsync(stream, SaveOptions.None, ct);
            File.Move(temporary, preferenceFile, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        if (!PreferenceMatches(preferenceFile, expectedScreens))
            throw new IOException("Arena's active Advanced Output XML could not be verified after writing it.");
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
