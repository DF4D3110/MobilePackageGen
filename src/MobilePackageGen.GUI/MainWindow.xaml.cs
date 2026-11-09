using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using MobilePackageGen;

namespace MobilePackageGen.GUI;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PackageRow> allItems = new();
    private IEnumerable<IDisk>? loadedDisks;
    private UpdateHistory.UpdateHistory? updateHistory;
    private readonly List<FMFileInfo> allFMFiles = new();
    private CompatibilityReport? compatibilityReport;

    public MainWindow()
    {
        InitializeComponent();
        OutputPathTextBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MobilePackageGen_Output");
        ItemsGrid.ItemsSource = allItems;
    }

    private async void OpenImages_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "镜像文件|*.ffu;*.vhd;*.vhdx;*.wim|所有文件|*.*",
            Multiselect = true,
            Title = "选择一个或多个镜像文件"
        };
        if (dialog.ShowDialog(this) != true) return;

        await LoadImagesAsync(dialog.FileNames);
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (paths is { Length: > 0 })
            await LoadImagesAsync(paths);
    }

    private async Task LoadImagesAsync(string[] paths)
    {
        try
        {
            SetBusy(true, "正在读取镜像...");
            SourceSummaryText.Text = $"正在加载 {paths.Length} 个输入源";
            var result = await Task.Run(() =>
            {
                var disks = DiskLoader.LoadDisks(paths);
                if (disks == null || !disks.Any())
                    throw new InvalidOperationException("没有找到可读取的分区或文件系统。");

                var history = BuildMetadataHandler.GetUpdateHistory(disks);
                var packages = PackageEnumerator.EnumerateAll(disks, OutputPathTextBox.Text);
                var report = ImageCompatibilityProbe.ProbeMany(paths);
                return (disks, history, packages, EnumerateFMFiles(disks), report);
            });

            loadedDisks = result.disks;
            updateHistory = result.history;
            allFMFiles.Clear();
            allFMFiles.AddRange(result.Item4);
            compatibilityReport = result.report;
            CompatibilityReportTextBox.Text = compatibilityReport.ToString();
            allItems.Clear();
            foreach (var package in result.packages)
                allItems.Add(PackageRow.FromPackage(package));
            foreach (var fm in result.Item4)
                allItems.Add(PackageRow.FromFM(fm));

            SourceSummaryText.Text = paths.Length == 1 ? Path.GetFileName(paths[0]) : $"{paths.Length} 个镜像输入源";
            StatusText.Text = $"已发现 {allItems.Count} 个包项目";
            ProgressBar.Value = 100;
            UpdateSelectionSummary();
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取失败";
            MessageBox.Show(this, ex.Message, "无法加载镜像", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (loadedDisks == null)
        {
            MessageBox.Show(this, "请先导入镜像。", "尚未加载内容", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedRows = allItems.Where(item => item.IsSelected).ToList();
        var selected = selectedRows.Where(item => item.Package != null).Select(item => item.Package!).ToList();
        var selectedFM = selectedRows.Where(item => item.FM != null).Select(item => item.FM!).ToList();
        if (selected.Count == 0 && selectedFM.Count == 0)
        {
            MessageBox.Show(this, "请至少选择一个包项目。", "没有选择内容", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var outputPath = OutputPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            MessageBox.Show(this, "请输入输出目录。", "输出目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(outputPath);
        try
        {
            SetBusy(true, "正在构建选中内容...");
            BuildOutputText.Text = outputPath;
            BuildSelectionText.Text = $"{selected.Count + selectedFM.Count} 个项目将被构建";
            ResultHeadlineText.Text = "正在构建...";
            ResultSummaryText.Text = $"正在生成 {selected.Count + selectedFM.Count} 个项目。";
            ResultOutputText.Text = outputPath;
            ResultLogTextBox.Text = "构建已启动...";
            ShowPage("results");
            await Task.Run(() => BuildSelected(selected, selectedFM, outputPath));
            ProgressBar.Value = 100;
            StatusText.Text = $"构建完成：{selected.Count + selectedFM.Count} 个项目";
            ResultHeadlineText.Text = "构建完成";
            var outputCount = Directory.Exists(outputPath) ? Directory.GetFiles(outputPath, "*", SearchOption.AllDirectories).Length : 0;
            ResultSummaryText.Text = $"已完成 {selected.Count + selectedFM.Count} 个项目，输出目录包含 {outputCount} 个文件。CAT 签名和结果校验尚未执行。";
            ResultLogTextBox.Text = $"输出目录：{outputPath}\n日志文件：{Path.Combine(outputPath, "build.log")}";
            MessageBox.Show(this, $"已生成 {selected.Count} 个项目。\n输出目录：{outputPath}", "构建完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "构建失败";
            ResultHeadlineText.Text = "构建失败";
            ResultSummaryText.Text = ex.Message;
            ResultLogTextBox.Text = ex.ToString();
            MessageBox.Show(this, ex.Message, "构建失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private void BuildSelected(List<PackageInfo> selected, List<FMFileInfo> selectedFM, string outputPath)
    {
        if (loadedDisks == null) return;
        if (WriteBuildLogCheckBox.IsChecked == true)
            Logging.SetLogFile(Path.Combine(outputPath, "build.log"));
        var cbsNames = selected.Where(package => package.Type == PackageType.CBS)
            .Select(package => Path.GetFileName(package.CabFileName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var spkgNames = selected.Where(package => package.Type == PackageType.SPKG)
            .Select(package => Path.GetFileName(package.CabFileName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var driverNames = selected.Where(package => package.Type == PackageType.Driver)
            .Select(package => Path.GetFileName(package.CabFileName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (cbsNames.Count > 0)
        {
            CBSBuilder.UseUncompressedManifests = UncompressedManifestsCheckBox.IsChecked == true;
            CBSBuilder.BuildCBS(loadedDisks, outputPath, updateHistory, cbsNames);
        }
        if (spkgNames.Count > 0)
            SPKGBuilder.BuildSPKG(loadedDisks, outputPath, updateHistory, spkgNames);
        if (driverNames.Count > 0)
            DriverBuilder.BuildDrivers(loadedDisks, outputPath, updateHistory, driverNames);
        ExtractFMFiles(selectedFM, outputPath);
        ExtractAppx(selected, outputPath);
    }

    private List<FMFileInfo> EnumerateFMFiles(IEnumerable<IDisk> disks)
    {
        var result = new List<FMFileInfo>();
        foreach (var disk in disks)
        foreach (var partition in disk.Partitions)
        {
            var fs = partition.FileSystem;
            if (fs == null) continue;
            foreach (var vendor in new[] { "Microsoft", "OEM" })
            {
                var path = Path.Combine(@"Windows\ImageUpdate\FeatureManifest", vendor);
                try
                {
                    if (!fs.DirectoryExists(path)) continue;
                    foreach (var file in fs.GetFiles(path, "*.xml", SearchOption.TopDirectoryOnly))
                        result.Add(new FMFileInfo
                        {
                            Name = Path.GetFileName(file),
                            SourcePath = file,
                            DestinationPath = Path.Combine("FMFiles", vendor, Path.GetFileName(file)),
                            Vendor = vendor,
                            PartitionName = partition.Name?.Replace("\0", "-") ?? "Unknown",
                            Size = fs.GetFileInfo(file).Length
                        });
                }
                catch { }
            }
        }
        return result;
    }

    private void ExtractFMFiles(IEnumerable<FMFileInfo> files, string outputPath)
    {
        foreach (var fm in files)
        foreach (var disk in loadedDisks ?? Enumerable.Empty<IDisk>())
        foreach (var partition in disk.Partitions)
        {
            var fs = partition.FileSystem;
            if (fs == null) continue;
            try
            {
                if (!fs.FileExists(fm.SourcePath)) continue;
                var destination = Path.Combine(outputPath, fm.DestinationPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var source = fs.OpenFile(fm.SourcePath, FileMode.Open, FileAccess.Read);
                using var target = File.Create(destination);
                source.CopyTo(target);
                break;
            }
            catch { }
        }
    }

    private void ExtractAppx(IEnumerable<PackageInfo> selected, string outputPath)
    {
        var names = selected.Where(p => p.Type == PackageType.Appx)
            .Select(p => p.Name.Split('_')[0] + "_" + p.Name.Split('_').Last())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0 || loadedDisks == null) return;
        var index = 0;
        foreach (var disk in loadedDisks)
        foreach (var partition in disk.Partitions)
        {
            var fs = partition.FileSystem;
            if (fs == null) continue;
            try
            {
                if (!fs.DirectoryExists("AppData") || !fs.DirectoryExists("WindowsApps")) continue;
                AppBuilder.ExtractAppLicenses(fs, outputPath, new List<AppxPackage>(), names);
                AppBuilder.ExtractAppPackages(fs, outputPath, names.Count, ref index, new List<AppxPackage>(), names);
            }
            catch (Exception ex) { Logging.Log($"App extraction error: {ex.Message}", LoggingLevel.Error); }
        }
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var path = OutputPathTextBox.Text.Trim();
        if (!Directory.Exists(path))
        {
            MessageBox.Show(this, "输出目录尚未生成。", "目录不存在", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshItems();
    private void TypeFilterComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshItems();
    private void ItemsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is PackageRow row)
            DetailsText.Text = row.Details;
        UpdateSelectionSummary();
    }

    private void RefreshItems()
    {
        if (ItemsGrid == null) return;
        var query = SearchTextBox.Text.Trim();
        var type = (TypeFilterComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString();
        var visible = allItems.Where(item =>
            (string.IsNullOrEmpty(query) || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(type) || type == "全部类型" || item.Type.Equals(type, StringComparison.OrdinalIgnoreCase))).ToList();
        ItemsGrid.ItemsSource = visible;
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        SelectionSummaryText.Text = $"{allItems.Count(item => item.IsSelected)} / {allItems.Count} 个项目";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        allItems.Clear();
        allFMFiles.Clear();
        loadedDisks = null;
        updateHistory = null;
        compatibilityReport = null;
        CompatibilityReportTextBox.Text = "尚未加载镜像。导入镜像后可查看只读能力矩阵。";
        SourceSummaryText.Text = "尚未加载镜像";
        DetailsText.Text = "选择一个项目查看详情。";
        StatusText.Text = "就绪";
        ProgressBar.Value = 0;
        UpdateSelectionSummary();
    }

    private void NavigateBrowse_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("browse");
        StatusText.Text = "当前页面：导入与浏览";
    }

    private void NavigateBuild_Click(object sender, RoutedEventArgs e)
    {
        BuildItemsGrid.ItemsSource = allItems.Where(item => item.IsSelected).ToList();
        BuildSelectionText.Text = $"{allItems.Count(item => item.IsSelected)} 个项目已选择";
        BuildOutputText.Text = OutputPathTextBox.Text;
        ShowPage("build");
        StatusText.Text = "当前页面：构建配置";
    }

    private void NavigateResults_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("results");
        StatusText.Text = "当前页面：构建结果";
    }

    private void NavigateCat_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("cat");
        StatusText.Text = "当前页面：CAT 签名";
        CheckCatEnvironment();
    }

    private void NavigateValidation_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("validation");
        StatusText.Text = "当前页面：最终校验";
    }

    private void NavigateCompatibility_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("compatibility");
        CompatibilityReportTextBox.Text = compatibilityReport?.ToString() ??
            "尚未加载镜像。导入镜像后可查看只读能力矩阵。";
        StatusText.Text = "当前页面：兼容性报告";
    }

    private void CheckCatEnvironment_Click(object sender, RoutedEventArgs e) => CheckCatEnvironment();

    private void CheckCatEnvironment()
    {
        var options = new CatBuilderOptions();
        var messages = new List<string>();
        messages.Add(string.IsNullOrWhiteSpace(options.MakecatPath)
            ? "阻塞：未配置 makecat.exe 路径。"
            : File.Exists(options.MakecatPath) ? $"makecat.exe：{options.MakecatPath}" : $"阻塞：makecat.exe 不存在：{options.MakecatPath}");
        messages.Add(string.IsNullOrWhiteSpace(options.SigntoolPath)
            ? "阻塞：未配置 signtool.exe 路径。"
            : File.Exists(options.SigntoolPath) ? $"signtool.exe：{options.SigntoolPath}" : $"阻塞：signtool.exe 不存在：{options.SigntoolPath}");
        messages.Add(string.IsNullOrWhiteSpace(options.CertificateThumbprint) && string.IsNullOrWhiteSpace(options.CertificatePath)
            ? "阻塞：未配置签名证书指纹或 PFX 路径。"
            : "证书配置已提供；执行签名时将继续验证证书可用性。");
        CatEnvironmentText.Text = string.Join("\n", messages);
    }

    private void RunValidation_Click(object sender, RoutedEventArgs e)
    {
        string outputRoot = OutputPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            ValidationReportTextBox.Text = "阻塞：未设置输出目录。";
            return;
        }

        var plans = new List<OutputPathPlan>();
        if (Directory.Exists(outputRoot))
        {
            foreach (string file in Directory.GetFiles(outputRoot, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(outputRoot, file);
                PackageOutputKind kind = relative.StartsWith("CBS\\", StringComparison.OrdinalIgnoreCase) ? PackageOutputKind.CBS :
                    relative.StartsWith("SPKG\\", StringComparison.OrdinalIgnoreCase) ? PackageOutputKind.SPKG :
                    relative.StartsWith("Driver\\", StringComparison.OrdinalIgnoreCase) ? PackageOutputKind.Driver :
                    relative.StartsWith("Appx\\", StringComparison.OrdinalIgnoreCase) ? PackageOutputKind.Appx :
                    relative.Equals("OEMInput.xml", StringComparison.OrdinalIgnoreCase) ? PackageOutputKind.OEMInput :
                    PackageOutputKind.FeatureManifest;
                plans.Add(OutputPathPlanner.Plan(outputRoot, kind, relative, preserveLeadingKind: false));
            }
        }

        var result = new FinalValidationService().Validate(outputRoot, plans);
        ValidationReportTextBox.Text = result.Diagnostics.Count == 0
            ? $"校验通过：{outputRoot}\n已检查 {plans.Count} 个输出文件。"
            : string.Join("\n", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message} {d.Path}"));
    }

    private void ShowPage(string page)
    {
        BrowsePage.Visibility = page == "browse" ? Visibility.Visible : Visibility.Collapsed;
        BuildPage.Visibility = page == "build" ? Visibility.Visible : Visibility.Collapsed;
        ResultsPage.Visibility = page == "results" ? Visibility.Visible : Visibility.Collapsed;
        CatPage.Visibility = page == "cat" ? Visibility.Visible : Visibility.Collapsed;
        ValidationPage.Visibility = page == "validation" ? Visibility.Visible : Visibility.Collapsed;
        CompatibilityPage.Visibility = page == "compatibility" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetBusy(bool busy, string status)
    {
        StatusText.Text = status;
        ProgressBar.IsIndeterminate = busy;
    }
}

public sealed class PackageRow : INotifyPropertyChanged
{
    private bool isSelected;
    public PackageInfo? Package { get; init; }
    public FMFileInfo? FM { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string Version { get; init; } = "";
    public string Partition { get; init; } = "";
    public string Details { get; init; } = "";

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static PackageRow FromPackage(PackageInfo package) => new()
    {
        Package = package,
        Name = package.Name,
        Type = package.TypeDisplay,
        Architecture = package.Architecture,
        Version = package.Version,
        Partition = package.PartitionName,
        Details = $"类型：{package.TypeDisplay}\n名称：{package.Name}\n架构：{package.Architecture}\n版本：{package.Version}\n分区：{package.PartitionName}\n来源：{package.SourceManifest}\n预计大小：{package.SizeDisplay}",
        IsSelected = package.Selected
    };

    public static PackageRow FromFM(FMFileInfo fm) => new()
    {
        FM = fm,
        Name = fm.Name,
        Type = "FM",
        Partition = fm.PartitionName,
        Details = $"类型：FM\n名称：{fm.Name}\n厂商：{fm.Vendor}\n分区：{fm.PartitionName}\n来源：{fm.SourcePath}\n大小：{fm.Size} B",
        IsSelected = fm.Selected
    };
}
