using System.Text;
using System.Text.Json;
using DiscUtils;
using Img2Ffu.Reader;

namespace MobilePackageGen;

public enum ImageContainerKind { Unknown, Ffu, Img, Vhd, Vhdx, Wim, Directory }
public enum PartitionTableKind { Unknown, Mbr, Gpt, SuperFloppy }
public enum ImageFileSystemKind { Unknown, Ntfs, Fat, ExFat }

public sealed record ImageCapabilityMatrix(
    string InputPath,
    bool Exists,
    long? Length,
    ImageContainerKind Container,
    PartitionTableKind PartitionTable,
    int? SectorSize,
    ImageFileSystemKind FileSystem,
    string? GenerationCandidate,
    IReadOnlyList<string> KeyDirectories,
    IReadOnlyList<string> Markers,
    IReadOnlyList<string> Diagnostics)
{
    public bool IsReadable => Exists && Diagnostics.All(x => !x.StartsWith("ERROR:", StringComparison.Ordinal));
}

public sealed record CompatibilityReport(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ImageCapabilityMatrix> Images,
    IReadOnlyList<string> OutputDiagnostics,
    bool CleanupSucceeded)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    public override string ToString()
    {
        var lines = new StringBuilder();
        lines.AppendLine($"Generated: {GeneratedAt:O}");
        foreach (var image in Images)
        {
            lines.AppendLine($"{image.InputPath}: {(image.Exists ? "readable" : "SKIP (missing)")}");
            lines.AppendLine($"  container={image.Container}, table={image.PartitionTable}, sector={image.SectorSize?.ToString() ?? "unknown"}, fs={image.FileSystem}");
            lines.AppendLine($"  generation={image.GenerationCandidate ?? "unknown"}");
            foreach (var marker in image.Markers) lines.AppendLine($"  marker: {marker}");
            foreach (var diagnostic in image.Diagnostics) lines.AppendLine($"  {diagnostic}");
        }
        foreach (var diagnostic in OutputDiagnostics) lines.AppendLine($"output: {diagnostic}");
        lines.AppendLine($"cleanup: {(CleanupSucceeded ? "succeeded" : "failed")}");
        return lines.ToString();
    }
}

public static class ImageCompatibilityProbe
{
    private static readonly string[] KnownPaths =
    [
        @"Windows\ImageUpdate\OEMInput.xml",
        @"OEMInput.xml",
        @"Windows\ImageUpdate\FeatureManifest",
        @"Windows\Packages",
        @"Windows\Servicing\Packages",
        @"Windows\ImageUpdate\Packages",
        @"EFI\Microsoft\Boot"
    ];

    public static ImageCapabilityMatrix Probe(string path, bool enumerateFileSystem = true)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            return new(path, false, null, ImageContainerKind.Unknown, PartitionTableKind.Unknown, null,
                ImageFileSystemKind.Unknown, null, [], [], [$"SKIP: input does not exist: {path}"]);

        if (Directory.Exists(path))
            return ProbeDirectory(path);

        var diagnostics = new List<string>();
        var markers = new List<string>();
        var keyDirectories = new List<string>();
        var container = ContainerFromExtension(path);
        long length = new FileInfo(path).Length;
        var table = PartitionTableKind.Unknown;
        int? sectorSize = null;
        var fs = ImageFileSystemKind.Unknown;

        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            (table, sectorSize) = ReadPartitionTable(stream);
            fs = DetectFileSystem(stream);
            if (container == ImageContainerKind.Unknown && LooksLikeFfu(path))
                container = ImageContainerKind.Ffu;
            if (container == ImageContainerKind.Ffu) markers.Add("FFU store probe requested");
            if (table == PartitionTableKind.Unknown && fs != ImageFileSystemKind.Unknown)
                table = PartitionTableKind.SuperFloppy;
        }
        catch (Exception ex)
        {
            diagnostics.Add($"ERROR: read probe failed: {ex.Message}");
        }

        if (enumerateFileSystem)
        {
            try
            {
                var disks = DiskLoader.LoadDisks([path]).ToList();
                foreach (var disk in disks)
                foreach (var partition in disk.Partitions)
                {
                    if (partition.FileSystem == null) continue;
                    foreach (string knownPath in KnownPaths)
                    {
                        if (partition.FileSystem.DirectoryExists(knownPath))
                        {
                            keyDirectories.Add($"{partition.Name}:\\{knownPath}");
                            if (knownPath.Contains("FeatureManifest", StringComparison.OrdinalIgnoreCase)) markers.Add("FeatureManifest");
                            if (knownPath.EndsWith("OEMInput.xml", StringComparison.OrdinalIgnoreCase)) markers.Add("OEMInput");
                            if (knownPath.Contains("Packages", StringComparison.OrdinalIgnoreCase)) markers.Add("Packages");
                        }
                    }
                }
                if (!markers.Contains("OEMInput", StringComparer.OrdinalIgnoreCase))
                    diagnostics.Add("WARNING: OEMInput was not discovered.");
                if (!markers.Contains("FeatureManifest", StringComparer.OrdinalIgnoreCase))
                    diagnostics.Add("WARNING: FeatureManifest/FM was not discovered.");
                if (!markers.Contains("Packages", StringComparer.OrdinalIgnoreCase))
                    diagnostics.Add("WARNING: package/FM content was not discovered.");
            }
            catch (Exception ex)
            {
                diagnostics.Add($"ERROR: partition/filesystem enumeration failed: {ex.Message}");
            }
        }

        string? generation = markers.Any(x => x.Equals("OEMInput", StringComparison.OrdinalIgnoreCase))
            ? "Windows Phone / OneCore candidate"
            : null;
        return new(path, true, length, container, table, sectorSize, fs, generation,
            keyDirectories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            markers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), diagnostics);
    }

    public static CompatibilityReport ProbeMany(IEnumerable<string> paths, bool enumerateFileSystem = true)
    {
        return new(DateTimeOffset.UtcNow, paths.Select(x => Probe(x, enumerateFileSystem)).ToArray(), [], true);
    }

    private static ImageCapabilityMatrix ProbeDirectory(string path)
    {
        var diagnostics = new List<string>();
        var markers = new List<string>();
        var directories = new List<string>();
        foreach (string knownPath in KnownPaths)
        {
            string full = Path.Combine(path, knownPath);
            if (Directory.Exists(full) || File.Exists(full))
            {
                directories.Add(knownPath);
                if (knownPath.Contains("FeatureManifest", StringComparison.OrdinalIgnoreCase)) markers.Add("FeatureManifest");
                if (knownPath.EndsWith("OEMInput.xml", StringComparison.OrdinalIgnoreCase)) markers.Add("OEMInput");
                if (knownPath.Contains("Packages", StringComparison.OrdinalIgnoreCase)) markers.Add("Packages");
            }
        }
        if (!markers.Contains("OEMInput", StringComparer.OrdinalIgnoreCase)) diagnostics.Add("WARNING: OEMInput was not discovered.");
        if (!markers.Contains("FeatureManifest", StringComparer.OrdinalIgnoreCase)) diagnostics.Add("WARNING: FeatureManifest/FM was not discovered.");
        if (!markers.Contains("Packages", StringComparer.OrdinalIgnoreCase)) diagnostics.Add("WARNING: package/FM content was not discovered.");
        return new(path, true, null, ImageContainerKind.Directory, PartitionTableKind.Unknown, null,
            ImageFileSystemKind.Unknown, markers.Count > 0 ? "Windows Phone / OneCore candidate" : null,
            directories, markers, diagnostics);
    }

    private static ImageContainerKind ContainerFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ffu" => ImageContainerKind.Ffu,
        ".img" => ImageContainerKind.Img,
        ".vhd" => ImageContainerKind.Vhd,
        ".vhdx" => ImageContainerKind.Vhdx,
        ".wim" => ImageContainerKind.Wim,
        _ => ImageContainerKind.Unknown
    };

    private static bool LooksLikeFfu(string path)
    {
        try { return FullFlashUpdateReaderStream.GetStoreCount(path) > 0; }
        catch { return false; }
    }

    private static ImageFileSystemKind DetectFileSystem(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        if (DiscUtils.Ntfs.NtfsFileSystem.Detect(stream)) return ImageFileSystemKind.Ntfs;
        stream.Seek(0, SeekOrigin.Begin);
        if (DiscUtils.Fat.FatFileSystem.Detect(stream)) return ImageFileSystemKind.Fat;
        return ImageFileSystemKind.Unknown;
    }

    private static (PartitionTableKind, int?) ReadPartitionTable(Stream stream)
    {
        byte[] sector = new byte[4096];
        stream.Seek(0, SeekOrigin.Begin);
        int read = stream.Read(sector, 0, sector.Length);
        if (read >= 512 && sector[510] == 0x55 && sector[511] == 0xAA)
        {
            bool hasMbr = Enumerable.Range(0, 4).Any(i => sector[446 + i * 16 + 4] != 0 && BitConverter.ToUInt32(sector, 446 + i * 16 + 12) != 0);
            return (hasMbr ? PartitionTableKind.Mbr : PartitionTableKind.SuperFloppy, 512);
        }
        if (read >= 520 && Encoding.ASCII.GetString(sector, 512, 8) == "EFI PART")
            return (PartitionTableKind.Gpt, 512);
        return (PartitionTableKind.Unknown, null);
    }
}

public sealed class TemporaryTestWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "MobilePackageGen", Guid.NewGuid().ToString("N"));
    public bool CleanupSucceeded { get; private set; }
    public TemporaryTestWorkspace() => Directory.CreateDirectory(Root);
    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); CleanupSucceeded = !Directory.Exists(Root); }
        catch (Exception ex) { CleanupSucceeded = false; Logging.Log($"Temporary workspace cleanup failed: {ex.Message}", LoggingLevel.Error); }
    }
}
