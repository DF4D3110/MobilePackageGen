namespace MobilePackageGen;

public enum PackageOutputKind
{
    CBS,
    SPKG,
    Driver,
    Appx,
    OEMInput,
    FeatureManifest,
    Other
}

public enum OutputDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record OutputDiagnostic(OutputDiagnosticSeverity Severity, string Message, string? Path = null);

public sealed record OutputPathPlan(
    string Root,
    PackageOutputKind Kind,
    string RelativePath,
    string FullPath)
{
    public string Directory => Path.GetDirectoryName(FullPath) ?? Root;
}

public sealed record LegacyOutputPathPlan(
    string SourcePath,
    string RelativePath,
    bool IsKnownRule,
    bool IsSupported,
    string Diagnostic);

public static class LegacyOutputPlanner
{
    public static LegacyOutputPathPlan PreserveUnknown(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("A source path is required.", nameof(sourcePath));
        if (OutputPathNormalizer.ContainsTraversal(sourcePath))
            throw new InvalidOperationException($"The legacy source path contains traversal: {sourcePath}");

        string relative = OutputPathNormalizer.Normalize(sourcePath);
        return new LegacyOutputPathPlan(sourcePath, relative, false, false,
            "Legacy path preserved verbatim for review; no modern DriveX/OEMInput/FM rule was applied.");
    }
}

public static class OutputPathNormalizer
{
    public static bool ContainsTraversal(string path) =>
        path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part == "..");

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        string value = path.Trim().Replace('/', '\\');
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            value = "UNC\\" + value[8..];
        else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            value = value[4..];
        else if (value.StartsWith(@"\\", StringComparison.Ordinal))
            value = "UNC\\" + value[2..];

        if (value.Length >= 2 && value[1] == ':')
            value = "Drive" + char.ToUpperInvariant(value[0]) + "\\" + value[2..].TrimStart('\\');

        value = value.TrimStart('\\');
        var parts = value.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(parts.Length);
        foreach (string part in parts)
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (result.Count > 0)
                    result.RemoveAt(result.Count - 1);
                continue;
            }
            result.Add(part);
        }
        return string.Join('\\', result);
    }

    public static string NormalizeRelative(string root, string path)
    {
        string normalized = Normalize(path);
        string normalizedRoot = Normalize(root).TrimEnd('\\');
        if (normalized.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (normalized.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase))
            return normalized[(normalizedRoot.Length + 1)..];
        return normalized;
    }
}

public static class OutputPathPlanner
{
    public static OutputPathPlan PlanPackage(string outputRoot, PackageOutputKind kind, string relativePath) =>
        Plan(outputRoot, kind, relativePath);

    public static OutputPathPlan Plan(string outputRoot, PackageOutputKind kind, string path,
        bool preserveLeadingKind = true)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
            throw new ArgumentException("An output root is required.", nameof(outputRoot));

        if (OutputPathNormalizer.ContainsTraversal(path))
            throw new InvalidOperationException($"The planned output path contains a traversal segment: {path}");
        string normalizedRoot = Path.GetFullPath(outputRoot);
        string relative = OutputPathNormalizer.Normalize(path);
        if (preserveLeadingKind && !relative.StartsWith(kind.ToString() + "\\", StringComparison.OrdinalIgnoreCase))
            relative = Path.Combine(kind.ToString(), relative);
        relative = relative.Replace('/', '\\').TrimStart('\\');
        string full = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        if (!full.StartsWith(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) && !full.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The planned output path escapes the output root.");
        return new OutputPathPlan(normalizedRoot, kind, relative, full);
    }
}

public static class OutputLayout
{
    public static OutputPathPlan Cbs(string root, string path) => OutputPathPlanner.PlanPackage(root, PackageOutputKind.CBS, path);
    public static OutputPathPlan Spkg(string root, string path) => OutputPathPlanner.PlanPackage(root, PackageOutputKind.SPKG, path);
    public static OutputPathPlan Driver(string root, string path) => OutputPathPlanner.PlanPackage(root, PackageOutputKind.Driver, path);
    public static OutputPathPlan Appx(string root, string path) => OutputPathPlanner.PlanPackage(root, PackageOutputKind.Appx, path);
    public static OutputPathPlan OemInput(string root, string path = "OEMInput.xml") =>
        OutputPathPlanner.Plan(root, PackageOutputKind.OEMInput, path, preserveLeadingKind: false);
    public static OutputPathPlan FeatureManifest(string root, string path) =>
        OutputPathPlanner.Plan(root, PackageOutputKind.FeatureManifest, path, preserveLeadingKind: false);
}

public sealed class OutputValidationResult
{
    public List<OutputDiagnostic> Diagnostics { get; } = [];
    public bool IsValid => Diagnostics.All(x => x.Severity != OutputDiagnosticSeverity.Error);
}

public static class OutputValidator
{
    public static OutputValidationResult Validate(string outputRoot, IEnumerable<OutputPathPlan> plans,
        bool requireFiles = true)
    {
        var result = new OutputValidationResult();
        if (!Directory.Exists(outputRoot))
        {
            result.Diagnostics.Add(new(OutputDiagnosticSeverity.Error, "Output directory does not exist.", outputRoot));
            return result;
        }

        foreach (OutputPathPlan plan in plans)
        {
            if (requireFiles && !File.Exists(plan.FullPath))
                result.Diagnostics.Add(new(OutputDiagnosticSeverity.Error, "Expected output file is missing.", plan.FullPath));
            else if (!Directory.Exists(plan.Directory))
                result.Diagnostics.Add(new(OutputDiagnosticSeverity.Warning, "Output parent directory is missing.", plan.Directory));
        }
        return result;
    }
}

public sealed class FinalValidationService
{
    public OutputValidationResult Validate(string outputRoot, IEnumerable<OutputPathPlan> plans,
        bool requireCatalogs = false)
    {
        OutputValidationResult result = OutputValidator.Validate(outputRoot, plans);
        if (requireCatalogs)
        {
            foreach (OutputPathPlan plan in plans.Where(x =>
                x.Kind is PackageOutputKind.CBS or PackageOutputKind.SPKG or PackageOutputKind.Driver))
            {
                string? directory = Path.GetDirectoryName(plan.FullPath);
                if (directory == null)
                    continue;
                string[] catalogs = Directory.Exists(directory)
                    ? Directory.GetFiles(directory, "*.cat", SearchOption.TopDirectoryOnly)
                    : [];
                if (catalogs.Length == 0)
                    result.Diagnostics.Add(new(OutputDiagnosticSeverity.Error,
                        "No catalog was produced for the package.", plan.FullPath));
            }

        }
        return result;
    }
}

public sealed record FinalValidationReport(
    string OutputRoot,
    IReadOnlyList<OutputDiagnostic> Diagnostics,
    IReadOnlyList<string> ExtraFiles)
{
    public bool IsValid => Diagnostics.All(x => x.Severity != OutputDiagnosticSeverity.Error);

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    public override string ToString()
    {
        string status = IsValid ? "VALID" : "INVALID";
        string lines = string.Join(Environment.NewLine,
            Diagnostics.Select(x => $"[{x.Severity}] {x.Message}{(x.Path == null ? string.Empty : $" ({x.Path})")}"));
        return $"{status}: {OutputRoot}{Environment.NewLine}{lines}";
    }
}
