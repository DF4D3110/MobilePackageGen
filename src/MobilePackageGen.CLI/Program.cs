namespace MobilePackageGen
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            Console.WriteLine(@"
Mobile Package Generator Tool
OneCore Image Designer Preview
");

            if (args.Length == 1 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                RunSelfTest();
                return;
            }

            if (args.Length >= 2 && string.Equals(args[0], "--probe", StringComparison.OrdinalIgnoreCase))
            {
                RunProbe(args[1..]);
                return;
            }

            // Elevate to TrustedInstaller when not already running as TI.
            if (!TrustedInstallerHelper.IsTrustedInstaller())
            {
                try
                {
                    Console.WriteLine("Relaunching under TrustedInstaller...");
                    TrustedInstallerHelper.ElevateSelf(typeof(Program).Assembly.Location, args);
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warning] Failed to elevate to TrustedInstaller: {ex.Message}");
                    Console.WriteLine("Continuing with current privileges; some operations may fail.");
                }
            }

            if (args.Length < 2)
            {
                PrintHelp();
                return;
            }

            // Optional switches (removed from the input list before resolving inputs/output):
            //   --uncompressed-manifests : decompress DCM manifests to plain XML in output cabinets
            List<string> argList = [.. args];

            if (argList.Remove("--uncompressed-manifests") || argList.Remove("/u"))
            {
                CBSBuilder.UseUncompressedManifests = true;
                Console.WriteLine("Manifests will be decompressed to plain XML (DCM compression disabled).");
            }

            string[] inputArgs = argList[..^1].ToArray();
            string outputFolder = argList[^1];

            // Route a copy of all logs into the build output directory.
            Logging.SetLogFile(Path.Combine(outputFolder, "build.log"));

            IEnumerable<IDisk> disks = DiskLoader.LoadDisks(inputArgs);

            if (!disks.Any())
            {
                PrintHelp();
                return;
            }

            BuildMetadataHandler.GetOEMInput(disks, outputFolder);
            BuildMetadataHandler.GetFeatureManifests(disks, outputFolder);

            List<AppxPackage> appList = BuildMetadataHandler.GetAppList(disks);

            AppBuilder.BuildApps(disks, outputFolder, appList);

            UpdateHistory.UpdateHistory? updateHistory = BuildMetadataHandler.GetUpdateHistory(disks);

            CBSBuilder.BuildCBS(disks, outputFolder, updateHistory);

            SPKGBuilder.BuildSPKG(disks, outputFolder, updateHistory);

            DriverBuilder.BuildDrivers(disks, outputFolder, updateHistory);

            var plans = Directory.Exists(outputFolder)
                ? Directory.GetFiles(outputFolder, "*", SearchOption.AllDirectories)
                    .Select(file => OutputPathPlanner.Plan(outputFolder, PackageOutputKind.Other,
                        Path.GetRelativePath(outputFolder, file), preserveLeadingKind: false))
                    .ToList()
                : [];
            OutputValidationResult validation = new FinalValidationService().Validate(outputFolder, plans);
            string validationPath = Path.Combine(outputFolder, "final-validation.txt");
            File.WriteAllText(validationPath, validation.IsValid
                ? $"VALID{Environment.NewLine}Checked {plans.Count} output files.{Environment.NewLine}"
                : string.Join(Environment.NewLine, validation.Diagnostics.Select(x => $"[{x.Severity}] {x.Message} {x.Path}")));
            if (!validation.IsValid)
                throw new InvalidOperationException($"Final output validation failed. See {validationPath}");

            Console.WriteLine("The operation completed successfully.");
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Remember to run the tool as Trusted Installer (TI).");
            Console.WriteLine();
            Console.WriteLine("You need to pass at least 2 parameters:");
            Console.WriteLine(@"	<Path to MainOS/Data/EFIESP> <Output folder CBSs>");
            Console.WriteLine("Examples:");
            Console.WriteLine(@"	 D: C:\OutputCabs");
            Console.WriteLine(@"	 D: E: F: C:\OutputCabs");
            Console.WriteLine();
            Console.WriteLine("Optional switch:  --uncompressed-manifests  (decompress DCM manifests to plain XML in output cabinets)");
            Console.WriteLine();
            Console.WriteLine("You need to pass 2 parameters:");
            Console.WriteLine(@"	<Path to FFU File> <Output folder CBSs>");
            Console.WriteLine("Example:");
            Console.WriteLine(@"	 D:\Flash.ffu C:\OutputCabs");
            Console.WriteLine();
            Console.WriteLine("You need to pass at least 2 parameters:");
            Console.WriteLine(@"	<Path to VHDx> <Output folder CBSs>");
            Console.WriteLine("Examples:");
            Console.WriteLine(@"	 D:\LUN0.vhdx C:\OutputCabs");
            Console.WriteLine(@"	 D:\LUN0.vhdx D:\LUN1.vhdx D:\LUN2.vhdx C:\OutputCabs");
            Console.WriteLine();
            Console.WriteLine("Run synthetic output/parser checks without an image: --self-test");
        }

        private static void RunSelfTest()
        {
            ParseResult<OemInputMetadata> parsed = OemInputParser.Parse(
                "<OEMInput><AdditionalFM>DriveX\\FM\\Microsoft\\base.xml</AdditionalFM></OEMInput>");
            if (!parsed.IsValid || parsed.Value.FeatureManifestPaths.Count != 1)
                throw new InvalidOperationException("OEMInput parser self-test failed.");

            bool traversalRejected = false;
            try { OutputPathPlanner.Plan(Path.GetTempPath(), PackageOutputKind.FeatureManifest, "..\\escape.xml", false); }
            catch (InvalidOperationException) { traversalRejected = true; }
            if (!traversalRejected)
                throw new InvalidOperationException("Output traversal self-test failed.");
            LegacyOutputPathPlan legacyPlan = LegacyOutputPlanner.PreserveUnknown(@"Windows\LegacyPackages\old.cab");
            if (legacyPlan.IsKnownRule || legacyPlan.IsSupported || legacyPlan.RelativePath != @"Windows\LegacyPackages\old.cab")
                throw new InvalidOperationException("Legacy output planning self-test failed.");
            string root = Path.Combine(Path.GetTempPath(), "MobilePackageGenSelfTest");
            if (!OutputLayout.Cbs(root, "sample.cab").RelativePath.Equals("CBS\\sample.cab", StringComparison.OrdinalIgnoreCase) ||
                !OutputLayout.OemInput(root).RelativePath.Equals("OEMInput.xml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Output layout self-test failed.");

            using (var workspace = new TemporaryTestWorkspace())
            {
                string mbr = Path.Combine(workspace.Root, "synthetic.img");
                using (FileStream stream = File.Create(mbr))
                {
                    stream.SetLength(1024 * 1024);
                    stream.Position = 510;
                    stream.WriteByte(0x55);
                    stream.WriteByte(0xAA);
                }
                ImageCapabilityMatrix capability = ImageCompatibilityProbe.Probe(mbr, enumerateFileSystem: false);
                if (capability.PartitionTable != PartitionTableKind.SuperFloppy)
                    throw new InvalidOperationException("Image probe self-test failed.");
            }

            Console.WriteLine("Self-test passed: parsers, output traversal protection, image probe, and cleanup.");
        }

        private static void RunProbe(string[] inputPaths)
        {
            bool enumerate = true;
            var paths = new List<string>();
            foreach (string arg in inputPaths)
            {
                if (arg.Equals("--no-enumerate", StringComparison.OrdinalIgnoreCase))
                    enumerate = false;
                else
                    paths.Add(arg);
            }
            if (paths.Count == 0)
            {
                Console.Error.WriteLine("Probe requires at least one image or directory path.");
                return;
            }

            CompatibilityReport report = ImageCompatibilityProbe.ProbeMany(paths, enumerate);
            Console.WriteLine(report);
            foreach (ImageCapabilityMatrix image in report.Images.Where(x => !x.Exists))
                Console.WriteLine($"SKIP: {image.InputPath}");
        }
    }
}