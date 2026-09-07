using System.Globalization;
using System.Xml.Linq;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public sealed class AdvancedOutputPresetGenerator
{
    private long _nextId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 100;

    public XDocument Generate(ConfigurationPlan plan, ArenaProduct product)
    {
        var groupIndices = plan.Decoders
            .Select((decoder, index) => (decoder.OutputName, Index: index + 1))
            .ToDictionary(x => x.OutputName, x => x.Index, StringComparer.OrdinalIgnoreCase);
        groupIndices["LED Wall"] = plan.Decoders.Count + 1;
        groupIndices["Show"] = plan.Decoders.Count + 2;
        return Generate(plan, product, groupIndices);
    }

    public XDocument Generate(ConfigurationPlan plan, ArenaProduct product, IReadOnlyDictionary<string, int> groupIndices)
    {
        if (plan.Decoders.Count == 0) throw new InvalidOperationException("At least one decoder is required.");

        var screens = new XElement("screens");
        screens.Add(CreateScreen("Show", GetGroupIndex(groupIndices, "Show"), plan.CompositionWidth, plan.CompositionHeight, plan, true, 1));
        screens.Add(CreateScreen("LED Wall", GetGroupIndex(groupIndices, "LED Wall"), plan.CompositionWidth, plan.CompositionHeight, plan, false, 1));
        for (var i = 0; i < plan.Decoders.Count; i++)
        {
            var decoder = plan.Decoders[i];
            screens.Add(CreateScreen(decoder.OutputName, GetGroupIndex(groupIndices, decoder.OutputName), decoder.Width, decoder.Height, plan, true, i + 2));
        }

        // Arena's preset loader expects an XmlState wrapper. AdvancedOutput.xml in
        // Preferences intentionally omits it, so that compact state cannot be used
        // directly as a loadable preset.
        var setup = new XElement("ScreenSetup", new XAttribute("name", "ScreenSetup"),
            new XElement("Params", new XAttribute("name", "ScreenSetupParams")),
            new XElement("CurrentCompositionTextureSize", new XAttribute("width", plan.CompositionWidth), new XAttribute("height", plan.CompositionHeight)),
            screens,
            CreateSoftEdging());

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("XmlState", new XAttribute("name", ArenaPaths.SafeFileName(plan.PresetName, "NDI Job")),
                new XElement("versionInfo",
                    new XAttribute("name", "Resolume Arena"),
                    new XAttribute("majorVersion", product.Major),
                    new XAttribute("minorVersion", product.Minor),
                    new XAttribute("microVersion", product.Micro),
                    new XAttribute("revision", product.Revision)),
                setup));
    }

    public async Task<string> SaveAsync(XDocument document, ConfigurationPlan plan, CancellationToken ct)
    {
        Directory.CreateDirectory(plan.PresetDirectory);
        var path = ArenaPaths.OutputPath(plan.PresetDirectory, plan.PresetName, ".xml");
        await AtomicFile.WriteXmlAsync(path, document, ct, backup: true);
        return path;
    }

    private static int GetGroupIndex(IReadOnlyDictionary<string, int> indices, string name) =>
        indices.TryGetValue(name, out var index) ? index : throw new InvalidOperationException($"Arena group '{name}' was not found after group creation.");

    private XElement CreateScreen(string name, int groupIndex, int width, int height, ConfigurationPlan plan, bool ndiOutput, int deviceOrdinal)
    {
        if (width is < 1 or > 32768 || height is < 1 or > 32768)
            throw new InvalidOperationException($"{name} has an invalid output resolution.");

        return new XElement("Screen", new XAttribute("name", name), new XAttribute("uniqueId", NextId()),
            new XElement("Params", new XAttribute("name", "Params"),
                StringParam("Name", "", name),
                ScalarParam("Enabled", "BOOL", "1", "1"),
                ScalarParam("Hidden", "BOOL", "0", "0")),
            new XElement("Params", new XAttribute("name", "Output"),
                RangeParam("Opacity", 1, 1, 0, 1),
                RangeParam("Brightness", 0, 0, -1, 1),
                RangeParam("Contrast", 0, 0, -1, 1),
                RangeParam("Red", 0, 0, -1, 1),
                RangeParam("Green", 0, 0, -1, 1),
                RangeParam("Blue", 0, 0, -1, 1)),
            new XElement("guides", CreateGuide(0), CreateGuide(1)),
            new XElement("layers", CreateSlice(name, groupIndex, plan.CompositionWidth, plan.CompositionHeight, width, height)),
            new XElement("OutputDevice", ndiOutput ? CreateNdiDevice(name, width, height, deviceOrdinal) : CreateVirtualDevice(name, width, height, deviceOrdinal)));
    }

    private XElement CreateSlice(string name, int groupIndex, int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        var fitted = FitRect(inputWidth, inputHeight, outputWidth, outputHeight);
        return new XElement("Slice", new XAttribute("uniqueId", NextId()),
            new XElement("Params", new XAttribute("name", "Common"),
                StringParam("Name", "Layer", name),
                ScalarParam("Enabled", "BOOL", "1", "1")),
            new XElement("Params", new XAttribute("name", "Input"),
                new XElement("ParamChoice", new XAttribute("name", "Input Source"), new XAttribute("default", "0:1"), new XAttribute("value", $"1:{groupIndex}"), new XAttribute("storeChoices", "0")),
                ScalarParam("Input Opacity", "BOOL", "1", "1"),
                ScalarParam("Input Bypass/Solo", "BOOL", "1", "1"),
                ScalarParam("SoftEdgeEnable", "BOOL", "0", "0")),
            Rect("InputRect", 0, 0, inputWidth, inputHeight),
            Rect("OutputRect", fitted.X, fitted.Y, fitted.Width, fitted.Height),
            new XElement("Warper",
                new XElement("Params", new XAttribute("name", "Warper"),
                    new XElement("ParamChoice", new XAttribute("name", "Point Mode"), new XAttribute("default", "PM_LINEAR"), new XAttribute("value", "PM_LINEAR"), new XAttribute("storeChoices", "0")),
                    ScalarParam("Flip", "UINT8", "0", "0")),
                new XElement("BezierWarper", new XAttribute("controlWidth", "4"), new XAttribute("controlHeight", "4"), GridVertices(fitted.X, fitted.Y, fitted.Width, fitted.Height)),
                new XElement("Homography", new XElement("src", Corners(fitted.X, fitted.Y, fitted.Width, fitted.Height)), new XElement("dst", Corners(fitted.X, fitted.Y, fitted.Width, fitted.Height)))));
    }

    private static XElement CreateGuide(int type) =>
        new("ScreenGuide", new XAttribute("name", "ScreenGuide"), new XAttribute("type", type),
            new XElement("Params", new XAttribute("name", "Params"),
                new XElement("ParamPixels", new XAttribute("name", "Image")),
                RangeParam("Opacity", 0.25, 0.25, 0, 1)));

    private static XElement CreateNdiDevice(string name, int width, int height, int ordinal) =>
        new("OutputDeviceNDI",
            new XAttribute("name", name),
            new XAttribute("deviceId", $"NDIOutput {ordinal}"),
            new XAttribute("idHash", StableHash64($"NDI|{ordinal}|{name}")),
            new XAttribute("width", width),
            new XAttribute("height", height),
            new XElement("Params", new XAttribute("name", "Params"),
                RangeParam("Width", width, width, 1, 32768),
                RangeParam("Height", height, height, 1, 32768),
                ScalarParam("Alpha Output", "BOOL", "1", "1"),
                new XElement("ParamChoice", new XAttribute("name", "Bit depth"), new XAttribute("T", "INT32"), new XAttribute("default", "0"), new XAttribute("value", "0"), new XAttribute("storeChoices", "0"))));

    private static XElement CreateVirtualDevice(string name, int width, int height, int ordinal) =>
        new("OutputDeviceVirtual",
            new XAttribute("name", name),
            new XAttribute("deviceId", $"VirtualOutput {ordinal}"),
            new XAttribute("idHash", StableHash64($"Virtual|{ordinal}|{name}")),
            new XAttribute("width", width),
            new XAttribute("height", height),
            new XElement("Params", new XAttribute("name", "Params"),
                RangeParam("Width", 1920, width, 1, 32768),
                RangeParam("Height", 1080, height, 1, 32768)));

    private static XElement CreateSoftEdging() =>
        new("SoftEdging",
            new XElement("Params", new XAttribute("name", "Soft Edge"),
                RangeParam("Gamma Red", 2, 2, 1, 3),
                RangeParam("Gamma Green", 2, 2, 1, 3),
                RangeParam("Gamma Blue", 2, 2, 1, 3),
                RangeParam("Power", 2, 2, 0.1, 10)));

    private static XElement StringParam(string name, string defaultValue, string value) =>
        new("Param", new XAttribute("name", name), new XAttribute("T", "STRING"), new XAttribute("default", defaultValue), new XAttribute("value", value));

    private static XElement ScalarParam(string name, string type, string defaultValue, string value) =>
        new("Param", new XAttribute("name", name), new XAttribute("T", type), new XAttribute("default", defaultValue), new XAttribute("value", value));

    private static XElement RangeParam(string name, double defaultValue, double value, double min, double max) =>
        new("ParamRange", new XAttribute("name", name), new XAttribute("T", "DOUBLE"), new XAttribute("default", F(defaultValue)), new XAttribute("value", F(value)),
            new XElement("PhaseSourceStatic", new XAttribute("name", "PhaseSourceStatic")),
            new XElement("BehaviourDouble", new XAttribute("name", "BehaviourDouble")),
            new XElement("ValueRange", new XAttribute("name", "defaultRange"), new XAttribute("min", F(min)), new XAttribute("max", F(max))),
            new XElement("ValueRange", new XAttribute("name", "minMax"), new XAttribute("min", F(min)), new XAttribute("max", F(max))),
            new XElement("ValueRange", new XAttribute("name", "startStop"), new XAttribute("min", F(min)), new XAttribute("max", F(max))));

    private static XElement Rect(string name, double x, double y, double width, double height) =>
        new(name, new XAttribute("orientation", "0"), Corners(x, y, width, height));

    private static IEnumerable<XElement> Corners(double x, double y, double width, double height) =>
        new[] { Vertex(x, y), Vertex(x + width, y), Vertex(x + width, y + height), Vertex(x, y + height) };

    private static XElement GridVertices(double left, double top, double width, double height)
    {
        var vertices = new XElement("vertices");
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
                vertices.Add(Vertex(left + width * x / 3d, top + height * y / 3d));
        return vertices;
    }

    private static (double X, double Y, double Width, double Height) FitRect(int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        var scale = Math.Min(outputWidth / (double)inputWidth, outputHeight / (double)inputHeight);
        var width = inputWidth * scale;
        var height = inputHeight * scale;
        return ((outputWidth - width) / 2d, (outputHeight - height) / 2d, width, height);
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

    private static XElement Vertex(double x, double y) => new("v", new XAttribute("x", F(x)), new XAttribute("y", F(y)));
    private static string F(double value) => value.ToString("0.################", CultureInfo.InvariantCulture);
    private long NextId() => Interlocked.Increment(ref _nextId);
}
