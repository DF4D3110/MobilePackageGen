using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using WPMT.Core.Catalog;

namespace MobilePackageGen;

public sealed class CatBuilderOptions
{
    public string MakecatPath { get; init; } = string.Empty;
    public string SigntoolPath { get; init; } = string.Empty;
    public string CertificateThumbprint { get; init; } = string.Empty;
    public string? CertificatePath { get; init; }
    public bool ValidateCertificate { get; init; } = true;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed record CatToolResult(string ToolPath, int ExitCode, string StandardOutput, string StandardError);
public sealed record CatBuildResult(string CatalogPath, string CdfPath, IReadOnlyList<CatToolResult> ToolResults);

public interface ICatProcessRunner
{
    CatToolResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout);
}

public sealed class CatBuilderEnvironmentException(string message) : InvalidOperationException(message);

public sealed class DefaultCatProcessRunner : ICatProcessRunner
{
    public CatToolResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo)
            ?? throw new CatBuilderEnvironmentException($"Unable to start catalog tool '{executable}'.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(true); } catch { }
            throw new CatBuilderEnvironmentException($"Catalog tool '{executable}' timed out.");
        }
        return new CatToolResult(executable, process.ExitCode, stdout, stderr);
    }
}

public sealed class CatBuilderService
{
    private readonly CatBuilderOptions options;
    private readonly ICatProcessRunner runner;

    public CatBuilderService(CatBuilderOptions? options = null, ICatProcessRunner? runner = null)
    {
        this.options = options ?? new CatBuilderOptions();
        this.runner = runner ?? new DefaultCatProcessRunner();
    }

    public CatBuildResult Build(string cdfPath, string catalogFullPath)
    {
        if (!OperatingSystem.IsWindows() && runner is DefaultCatProcessRunner)
            throw new PlatformNotSupportedException("Catalog building requires Windows makecat.exe and signtool.exe.");
        RequireTool(options.MakecatPath, "makecat");
        RequireTool(options.SigntoolPath, "signtool");
        if (!File.Exists(cdfPath))
            throw new FileNotFoundException("The catalog definition file was not found.", cdfPath);
        string? outputDirectory = Path.GetDirectoryName(catalogFullPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("The catalog output path must include a directory.", nameof(catalogFullPath));
        Directory.CreateDirectory(outputDirectory);
        ValidateCertificate();

        var results = new List<CatToolResult>();
        results.Add(RunAndLog(options.MakecatPath, ["-v", cdfPath]));
        EnsureCatalog(catalogFullPath, "makecat");
        var signArguments = new List<string> { "sign" };
        if (!string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            signArguments.Add("/f");
            signArguments.Add(options.CertificatePath);
        }
        else
        {
            signArguments.Add("/sha1");
            signArguments.Add(options.CertificateThumbprint);
        }
        signArguments.Add("/fd");
        signArguments.Add("SHA256");
        signArguments.Add(catalogFullPath);
        results.Add(RunAndLog(options.SigntoolPath, signArguments));
        EnsureCatalog(catalogFullPath, "signtool");
        return new CatBuildResult(catalogFullPath, cdfPath, results);
    }

    private CatToolResult RunAndLog(string executable, IReadOnlyList<string> arguments)
    {
        CatToolResult result = runner.Run(executable, arguments, options.Timeout);
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            Logging.Log(result.StandardOutput.TrimEnd(), LoggingLevel.Information);
        if (!string.IsNullOrWhiteSpace(result.StandardError))
            Logging.Log(result.StandardError.TrimEnd(), LoggingLevel.Warning);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed with exit code {result.ExitCode}.\n{result.StandardError}");
        return result;
    }

    private void ValidateCertificate()
    {
        if (!options.ValidateCertificate || !string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            if (!string.IsNullOrWhiteSpace(options.CertificatePath) && !File.Exists(options.CertificatePath))
                throw new CatBuilderEnvironmentException($"Signing certificate was not found: {options.CertificatePath}");
            return;
        }
        if (string.IsNullOrWhiteSpace(options.CertificateThumbprint))
            throw new CatBuilderEnvironmentException("No signing certificate thumbprint was configured.");
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        string thumbprint = options.CertificateThumbprint.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (!store.Certificates.Cast<X509Certificate2>().Any(x =>
            string.Equals(x.Thumbprint?.Replace(" ", string.Empty, StringComparison.Ordinal), thumbprint, StringComparison.OrdinalIgnoreCase)))
            throw new CatBuilderEnvironmentException($"Signing certificate '{options.CertificateThumbprint}' was not found in CurrentUser\\My.");
    }

    private static void RequireTool(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new CatBuilderEnvironmentException($"Configured {name} executable was not found: {path}");
    }

    private static void EnsureCatalog(string path, string tool)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"{tool} did not produce a non-empty catalog: {path}");
    }
}

public static class CatBuilder
{
    public const string TestCatSignerThumbprint = "BC92FF5B9634EA707AD592A38C871B29C9A8216D";
    public static string MakecatPath { get; set; } = string.Empty;
    public static string SigntoolPath { get; set; } = string.Empty;
    public static string CertificateThumbprint { get; set; } = string.Empty;
    public static bool ValidateCertificate { get; set; } = true;

    public static void WriteCdf(string cdfPath, string catalogName, string resultDir, IEnumerable<CatalogFileEntry> files)
    {
        CdfBuilder cdf = new(cdfPath, catalogName, resultDir, 0x00010001, "SHA256")
        { NonConformantFilesFallbackFlat = false, PageHashes = false, CatalogVersion = 2 };
        foreach (CatalogFileEntry file in files)
            cdf.Hashes.Add(new CatalogHash(file.Tag, file.FilePath));
        cdf.Save();
    }

    public static CatBuildResult BuildCatalog(string cdfPath, string catalogFullPath) =>
        new CatBuilderService(new CatBuilderOptions { MakecatPath = MakecatPath, SigntoolPath = SigntoolPath,
            CertificateThumbprint = CertificateThumbprint, ValidateCertificate = ValidateCertificate }).Build(cdfPath, catalogFullPath);

    public static CatBuildResult BuildCatalog(string cdfPath, string catalogName, string resultDir, IEnumerable<CatalogFileEntry> files)
    {
        WriteCdf(cdfPath, catalogName, resultDir, files);
        return BuildCatalog(cdfPath, Path.Combine(resultDir, catalogName));
    }
}

public readonly record struct CatalogFileEntry(string Tag, string FilePath);
