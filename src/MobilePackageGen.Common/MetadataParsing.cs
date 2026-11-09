using System.Xml.Linq;
using System.Xml;

namespace MobilePackageGen;

public sealed record OemInputMetadata(IReadOnlyList<string> FeatureManifestPaths);
public sealed record FeatureManifestMetadata(IReadOnlyList<AppxPackage> AppxPackages);

public static class OemInputParser
{
    public static ParseResult<OemInputMetadata> ParseFile(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(new OemInputMetadata([]),
                [new(OutputDiagnosticSeverity.Error, $"Unable to read OEMInput.xml: {ex.Message}", path)]);
        }
    }

    public static ParseResult<OemInputMetadata> Parse(string content)
    {
        var diagnostics = new List<OutputDiagnostic>();
        var paths = new List<string>();
        try
        {
            XDocument document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
            foreach (XElement element in document.Descendants().Where(x =>
                x.Name.LocalName.Equals("AdditionalFM", StringComparison.OrdinalIgnoreCase)))
            {
                string value = element.Value.Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    diagnostics.Add(new(OutputDiagnosticSeverity.Warning, "An AdditionalFM element is empty."));
                    continue;
                }
                if (OutputPathNormalizer.ContainsTraversal(value))
                {
                    diagnostics.Add(new(OutputDiagnosticSeverity.Error,
                        "An AdditionalFM path contains a traversal segment.", value));
                    continue;
                }
                paths.Add(OutputPathNormalizer.Normalize(value));
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            diagnostics.Add(new(OutputDiagnosticSeverity.Error, $"OEMInput.xml is invalid: {ex.Message}"));
        }
        return new(new OemInputMetadata(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()), diagnostics);
    }

    public static ParseResult<OemInputMetadata> Parse(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        return Parse(reader.ReadToEnd());
    }
}

public static class FeatureManifestParser
{
    public static ParseResult<FeatureManifestMetadata> Parse(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(new FeatureManifestMetadata([]),
                [new(OutputDiagnosticSeverity.Error, $"Unable to read feature manifest: {ex.Message}", path)]);
        }
    }

    public static ParseResult<FeatureManifestMetadata> Parse(Stream stream)
    {
        var diagnostics = new List<OutputDiagnostic>();
        var packages = new List<AppxPackage>();
        try
        {
            XDocument document = XDocument.Load(stream, LoadOptions.None);
            foreach (XElement package in document.Descendants().Where(x =>
                x.Name.LocalName.Equals("AppXPackages", StringComparison.OrdinalIgnoreCase)).Elements())
            {
                string name = package.Attribute("Name")?.Value ?? string.Empty;
                string path = package.Attribute("Path")?.Value ?? string.Empty;
                string license = package.Attribute("LicenseFile")?.Value ?? string.Empty;
                string id = package.Attribute("ID")?.Value ?? string.Empty;
                string cpu = package.Attribute("CPUType")?.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id))
                    diagnostics.Add(new(OutputDiagnosticSeverity.Warning, "An AppXPackages entry has no name or ID."));
                packages.Add(new AppxPackage(name, OutputPathNormalizer.Normalize(path), license, id,
                    cpu.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            diagnostics.Add(new(OutputDiagnosticSeverity.Error, $"Feature manifest is invalid: {ex.Message}"));
        }
        return new(new FeatureManifestMetadata(packages), diagnostics);
    }
}

public sealed record ParseResult<T>(T Value, IReadOnlyList<OutputDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(x => x.Severity != OutputDiagnosticSeverity.Error);
}
