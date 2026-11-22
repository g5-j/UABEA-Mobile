using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Sockets;

namespace UABEA.OriginalHost;

[Activity(
    Label = "UABEA Original",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation |
                           Android.Content.PM.ConfigChanges.ScreenSize |
                           Android.Content.PM.ConfigChanges.UiMode)]
public sealed class MainActivity : Android.App.Activity
{
    const int PickFileRequest = 4101;
    const int SaveFileRequest = 4102;
    const int OutputFolderRequest = 4103;
    const int VncPort = 5900;
    const string RuntimeInstallVersion = "uabea-runtime-v5";

    TextView _status = null!;
    TextView _connection = null!;
    string? _lastImportedFile;
    Android.Net.Uri? _outputTreeUri;
    Task<bool>? _installTask;
    bool _startingRuntime;
    FileSystemWatcher? _homeWatcher;
    CancellationTokenSource? _syncDebounce;
    readonly SemaphoreSlim _syncLock = new(1, 1);
    readonly Dictionary<string, System.Diagnostics.Process> _runtimeProcesses = new(StringComparer.OrdinalIgnoreCase);
    static readonly object RuntimeLogLock = new();

    string RuntimeDirectory => Path.Combine(FilesDir!.AbsolutePath, "runtime");
    string HomeDirectory => Path.Combine(FilesDir!.AbsolutePath, "linux-home");
    string RootfsDirectory => Path.Combine(RuntimeDirectory, "rootfs");
    string NativeDirectory => ApplicationInfo!.NativeLibraryDir!;

    string ProotPath => Path.Combine(NativeDirectory, "libproot.so");
    string LoaderPath => Path.Combine(NativeDirectory, "libloader.so");
    string Box64Path => Path.Combine(NativeDirectory, "libbox64.so");
    bool IsNativeX64 => RuntimeInformation.ProcessArchitecture == Architecture.X64;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        InitializeRuntimeLogging();
        WriteRuntimeLog("APP", $"Application started. Package={PackageName}; FilesDir={FilesDir?.AbsolutePath}; NativeLibraryDir={NativeDirectory}; Android={Build.VERSION.Release} (API {Build.VERSION.SdkInt}).");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteRuntimeLog("FATAL", "Unhandled exception: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteRuntimeLog("FATAL", "Unobserved task exception: " + e.Exception);
            e.SetObserved();
        };
        BuildHostUi();
        var savedTree = GetSharedPreferences("uabea-host", FileCreationMode.Private)!
            .GetString("output_tree_uri", null);
        if (!string.IsNullOrWhiteSpace(savedTree) && Android.Net.Uri.Parse(savedTree) is { } savedUri)
            _outputTreeUri = savedUri;
        _installTask = Task.Run(InstallRuntimeAssetsAsync);
    }

    void InitializeRuntimeLogging()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(RuntimeDirectory, "logs"));
            Directory.CreateDirectory(Path.Combine(RuntimeDirectory, "proot-tmp"));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("UABEA.Runtime", "Could not initialize file logging: " + ex);
        }
    }

    void WriteRuntimeLog(string category, string message)
    {
        var line = $"[{DateTime.UtcNow:O}] [{category}] {message}{System.Environment.NewLine}";
        try
        {
            lock (RuntimeLogLock)
            {
                Directory.CreateDirectory(Path.Combine(RuntimeDirectory, "logs"));
                File.AppendAllText(Path.Combine(RuntimeDirectory, "logs", "runtime.log"), line);
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("UABEA.Runtime", "File log write failed: " + ex);
            Android.Util.Log.Error("UABEA.Runtime", line);
        }
    }

    void BuildHostUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));

        var header = new TextView(this)
        {
            Text = "UABEA ORIGINAL",
            TextSize = 22,
            Gravity = GravityFlags.CenterVertical
        };
        root.AddView(header, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(52)));

        _status = new TextView(this)
        {
            Text = "Installing Linux runtime…",
            TextSize = 13
        };
        root.AddView(_status, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(64)));

        var connectionTitle = new TextView(this)
        {
            Text = "VNC CONNECTION",
            TextSize = 14
        };
        root.AddView(connectionTitle);

        _connection = new TextView(this)
        {
            Text = "127.0.0.1:5900",
            TextSize = 20,
            Typeface = Android.Graphics.Typeface.Monospace
        };
        _connection.SetTextIsSelectable(true);
        root.AddView(_connection, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(48)));

        var actions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        AddAction(actions, "Start", StartOriginalRuntime);
        AddAction(actions, "Stop", StopRuntimeFromUi);
        AddAction(actions, "Copy", CopyVncAddress);
        root.AddView(actions, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(52)));

        var files = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        AddAction(files, "Open file", PickUnityFile);
        AddAction(files, "Output folder", PickOutputFolder);
        AddAction(files, "Bundle test", RunBundleRoundTripSelfTest);
        root.AddView(files, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(52)));

        var help = new TextView(this)
        {
            Text = "Start the runtime, then connect with an external VNC Viewer to 127.0.0.1:5900. Choose or create the UABEA folder with Output folder; saved files sync there automatically. VNC accepts local connections only.",
            TextSize = 13
        };
        root.AddView(help, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        SetContentView(root);
    }

    void AddAction(LinearLayout row, string title, Action action)
    {
        var button = new Button(this) { Text = title, TextSize = 12 };
        button.Click += (_, _) => action();
        row.AddView(button, new LinearLayout.LayoutParams(0, Dp(48), 1));
    }

    int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + 0.5f);

    async Task<bool> InstallRuntimeAssetsAsync()
    {
        WriteRuntimeLog("INSTALL", $"Runtime installation started. Version={RuntimeInstallVersion}; runtime={RuntimeDirectory}; rootfs={RootfsDirectory}.");
        try
        {
            Directory.CreateDirectory(RuntimeDirectory);
            Directory.CreateDirectory(HomeDirectory);
            Directory.CreateDirectory(Path.Combine(RuntimeDirectory, "logs"));
            Directory.CreateDirectory(Path.Combine(RuntimeDirectory, "proot-tmp"));
            WriteRuntimeLog("INSTALL", "Runtime directories created. Beginning original UABEA payload installation.");

            await InstallArchiveAtomicallyAsync(
                "uabea-original.tar.gz",
                Path.Combine(RuntimeDirectory, "original"),
                new[] { "UABEAvalonia.dll", "UABEAvalonia.deps.json", "UABEAvalonia.runtimeconfig.json" });
            WriteRuntimeLog("INSTALL", "Original UABEA payload installed and verified.");

            WriteRuntimeLog("INSTALL", "Beginning Linux x64 rootfs installation."); 
            await InstallArchiveAtomicallyAsync(
                "linux-x64-rootfs.tar.gz",
                RootfsDirectory,
                new[] { "opt/dotnet/dotnet", "usr/bin/Xvfb", "usr/bin/x11vnc", "etc/debian_version" });

            WriteRuntimeLog("INSTALL", "Linux x64 rootfs installed and verified.");
            var dotnetPath = Path.Combine(RootfsDirectory, "opt", "dotnet", "dotnet");
            WriteRuntimeLog("VERIFY", $"dotnet exists={File.Exists(dotnetPath)}; bytes={(File.Exists(dotnetPath) ? new FileInfo(dotnetPath).Length : 0)}; path={dotnetPath}.");
            Directory.CreateDirectory(Path.Combine(RootfsDirectory, "home", "uabea"));

            foreach (var nativeBinary in new[] { ProotPath, LoaderPath, Box64Path })
            {
                if (!File.Exists(nativeBinary))
                    throw new FileNotFoundException(
                        "Android did not extract a required executable from the APK native libraries: " + nativeBinary);
            }

            StartHomeFileWatcher();
            WriteRuntimeLog("INSTALL", "Runtime installation completed successfully. Required files and Android native libraries are present.");
            RunOnUiThread(() =>
                _status.Text = "Linux runtime installed and verified. Press Start, then connect with a VNC app.");
            return true;
        }
        catch (Exception ex)
        {
            WriteRuntimeLog("INSTALL-ERROR", ex.ToString());
            RunOnUiThread(() => _status.Text = "Runtime installation failed: " + ex.Message);
            return false;
        }
    }

    async Task InstallArchiveAtomicallyAsync(string assetName, string destination, string[] requiredFiles)
    {
        WriteRuntimeLog("ARCHIVE", $"Checking archive '{assetName}' for destination '{destination}'.");
        var backup = destination + ".previous";
        if (!Directory.Exists(destination) && Directory.Exists(backup))
        {
            Directory.Move(backup, destination);
            WriteRuntimeLog("ARCHIVE", $"Recovered previous installation after an interrupted replacement: {destination}.");
        }

        var marker = Path.Combine(destination, ".install-complete");
        var valid = File.Exists(marker) &&
            File.ReadAllText(marker).StartsWith(RuntimeInstallVersion, StringComparison.Ordinal) && requiredFiles.All(
            item => File.Exists(Path.Combine(destination, item.Replace('/', Path.DirectorySeparatorChar))));
        if (valid)
        {
            WriteRuntimeLog("ARCHIVE", $"Existing installation is valid: {destination}.");
            return;
        }

        WriteRuntimeLog("ARCHIVE", $"Extraction required for '{assetName}'.");
        var staging = destination + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            await ExtractTarGzAssetAsync(assetName, staging);
            WriteRuntimeLog("ARCHIVE", $"Extraction finished for '{assetName}'. Verifying required files.");
            foreach (var item in requiredFiles)
            {
                var required = Path.Combine(staging, item.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(required))
                    throw new InvalidDataException("Archive is incomplete; missing " + item);
                WriteRuntimeLog("VERIFY", $"Required archive file present: {item}; bytes={new FileInfo(required).Length}.");
                if (item == "opt/dotnet/dotnet")
                {
                    // The .NET apphost is normally well below 1 MiB. Validate its
                    // ELF header and architecture instead of imposing an invalid size floor.
                    var dotnetInfo = new FileInfo(required);
                    if (dotnetInfo.Length < 64 * 1024)
                        throw new InvalidDataException(
                            $"Linux .NET runtime executable is truncated ({dotnetInfo.Length} bytes).");

                    using var dotnetStream = File.OpenRead(required);
                    var elfHeader = new byte[20];
                    var headerRead = await dotnetStream.ReadAsync(elfHeader);
                    if (headerRead != elfHeader.Length ||
                        elfHeader[0] != 0x7f || elfHeader[1] != (byte)'E' ||
                        elfHeader[2] != (byte)'L' || elfHeader[3] != (byte)'F' ||
                        elfHeader[4] != 2 || elfHeader[5] != 1 ||
                        elfHeader[18] != 0x3e || elfHeader[19] != 0x00)
                        throw new InvalidDataException(
                            "Linux .NET runtime executable is not a valid 64-bit little-endian x86_64 ELF binary.");
                }
            }

            await File.WriteAllTextAsync(Path.Combine(staging, ".install-complete"),
                RuntimeInstallVersion + "\n" + assetName + "\n" + DateTime.UtcNow.ToString("O"));

            var movedPrevious = false;
            if (Directory.Exists(destination))
            {
                if (Directory.Exists(backup))
                    Directory.Delete(backup, recursive: true);
                Directory.Move(destination, backup);
                movedPrevious = true;
            }

            try
            {
                Directory.Move(staging, destination);
            }
            catch
            {
                if (!Directory.Exists(destination) && movedPrevious && Directory.Exists(backup))
                    Directory.Move(backup, destination);
                throw;
            }

            if (movedPrevious)
            {
                try { Directory.Delete(backup, recursive: true); }
                catch (Exception cleanupEx)
                {
                    WriteRuntimeLog("ARCHIVE-WARN", $"New installation committed, but previous-installation cleanup failed: {cleanupEx.Message}");
                }
            }

            WriteRuntimeLog("ARCHIVE", $"Installation committed with rollback protection: {destination}.");
        }
        catch (Exception ex)
        {
            WriteRuntimeLog("ARCHIVE-ERROR", $"Archive '{assetName}' failed: {ex}");
            // Keep staging data and its diagnostic evidence for inspection; next
            // install attempt will safely clear and recreate this staging directory.
            throw;
        }
    }

    async Task ExtractTarGzAssetAsync(string assetName, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        var fullRoot = Path.GetFullPath(destinationRoot);

        WriteRuntimeLog("EXTRACT", $"Opening packaged asset '{assetName}' into '{destinationRoot}'.");
        using var input = Assets!.Open(assetName);
        WriteRuntimeLog("EXTRACT", "Asset stream opened successfully.");
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        TarEntry? entry;
        long extracted = 0;
        long extractedBytes = 0;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            var relative = entry.Name.TrimStart('.', '/', '\\')
                .Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(relative)) continue;
            var output = Path.GetFullPath(Path.Combine(fullRoot, relative));
            if (!output.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(output, fullRoot, StringComparison.Ordinal))
                throw new InvalidDataException("Unsafe path in runtime archive.");

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(output);
                try { File.SetUnixFileMode(output, entry.Mode); } catch { }
                continue;
            }

            if (entry.EntryType == TarEntryType.SymbolicLink)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                if (File.Exists(output) || Directory.Exists(output))
                {
                    try { File.Delete(output); } catch { Directory.Delete(output, recursive: true); }
                }
                File.CreateSymbolicLink(output, entry.LinkName);
                continue;
            }

            if (entry.EntryType == TarEntryType.HardLink)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                var linkRelative = entry.LinkName.TrimStart('.', '/', '\\')
                    .Replace('/', Path.DirectorySeparatorChar);
                var linkTarget = Path.GetFullPath(Path.Combine(fullRoot, linkRelative));
                if (linkTarget.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && File.Exists(linkTarget))
                    File.Copy(linkTarget, output, overwrite: true);
                continue;
            }

            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var target = File.Create(output);
                if (entry.DataStream is not null)
                {
                    var before = target.Length;
                    await entry.DataStream.CopyToAsync(target);
                    extractedBytes += target.Length - before;
                }
                try { File.SetUnixFileMode(output, entry.Mode); } catch (Exception modeEx) {
                    WriteRuntimeLog("EXTRACT-WARN", $"Could not apply mode to '{relative}': {modeEx.Message}");
                }
                extracted++;
                if (extracted % 1000 == 0)
                {
                    WriteRuntimeLog("EXTRACT", $"Progress for '{assetName}': files={extracted}; bytes={extractedBytes}.");
                    RunOnUiThread(() => _status.Text = "Installing Linux runtime… " + extracted + " files");
                }
            }
        }
        WriteRuntimeLog("EXTRACT", $"Completed '{assetName}': regular files={extracted}; extracted bytes={extractedBytes}.");
    }

    async void StartOriginalRuntime()
    {
        if (_startingRuntime)
        {
            _status.Text = "Runtime startup is already in progress.";
            return;
        }

        var existingApp = GetRunningProcess("UABEA");
        if (existingApp is not null)
        {
            _status.Text = "UABEA is already running. Connect using your VNC app.";
            return;
        }

        _startingRuntime = true;
        WriteRuntimeLog("START", "Start button pressed.");
        try
        {
            if (_installTask is not null && !await _installTask)
                throw new InvalidOperationException("Linux runtime installation failed. Review runtime/logs/runtime.log before retrying.");

            var requiredComponents = new List<string> {
                ProotPath, LoaderPath,
                Path.Combine(RootfsDirectory, "opt", "dotnet", "dotnet"),
                Path.Combine(RuntimeDirectory, "original", "UABEAvalonia.dll")
            };
            if (!IsNativeX64)
                requiredComponents.Add(Box64Path);

            foreach (var required in requiredComponents)
            {
                if (!File.Exists(required))
                {
                    WriteRuntimeLog("START-ERROR", $"Missing runtime component: {required}");
                    throw new FileNotFoundException("Missing runtime component: " + required);
                }

                WriteRuntimeLog("START", $"Required component present: {required}; bytes={new FileInfo(required).Length}.");
            }

            var sanityName = IsNativeX64 ? "Native-X64-Sanity" : "Box64-Sanity";
            _status.Text = IsNativeX64
                ? "Checking native Linux x86_64 execution…"
                : "Checking Box64 Linux execution…";
            var sanity = StartGuestProcess(sanityName, "/usr/bin/true");
            await sanity.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (sanity.ExitCode != 0)
                throw new IOException(
                    $"Linux x86_64 sanity executable failed (exit code {sanity.ExitCode}). Check runtime/logs/{sanityName}.log.");

            WriteRuntimeLog("START", IsNativeX64
                ? "Native Linux x86_64 sanity executable completed successfully."
                : "Box64 x86_64 sanity executable completed successfully.");

            _status.Text = "Starting Linux desktop…";
            if (GetRunningProcess("Xvfb") is null)
            {
                var socketPath = Path.Combine(RootfsDirectory, "tmp", ".X11-unix", "X1");
                try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
                StartGuestProcess("Xvfb", "/usr/bin/Xvfb",
                    ":1", "-screen", "0", "1280x800x24", "-nolisten", "tcp");
                if (!await WaitForFileAsync(socketPath, 20000))
                    throw new IOException("Xvfb did not create its display socket. Check runtime/logs/Xvfb.log.");
            }

            _status.Text = "Starting local VNC server…";
            if (GetRunningProcess("x11vnc") is null)
            {
                StartGuestProcess("x11vnc", "/usr/bin/x11vnc",
                    "-display", ":1", "-forever", "-shared", "-nopw",
                    "-localhost", "-rfbport", VncPort.ToString());
            }

            if (!await WaitForPortAsync(VncPort, 15000))
                throw new IOException("VNC server did not listen on 127.0.0.1:5900. Check runtime/logs/x11vnc.log.");

            _status.Text = "Launching original UABEA…";
            var app = StartGuestProcess("UABEA", "/opt/dotnet/dotnet",
                "/opt/uabea/UABEAvalonia.dll");

            await Task.Delay(1500);
            if (app.HasExited)
                throw new IOException("UABEA exited during startup. Check runtime/logs/UABEA.log.");

            _status.Text = "UABEA is running. Connect your VNC app to 127.0.0.1:5900.";
        }
        catch (Exception ex)
        {
            WriteRuntimeLog("START-ERROR", ex.ToString());
            _status.Text = "Runtime start failed: " + ex.Message;
            StopRuntimeProcesses();
        }
        finally
        {
            _startingRuntime = false;
        }
    }

    System.Diagnostics.Process? GetRunningProcess(string name)
    {
        if (!_runtimeProcesses.TryGetValue(name, out var process))
            return null;
        try
        {
            if (!process.HasExited) return process;
        }
        catch { }
        _runtimeProcesses.Remove(name);
        try { process.Dispose(); } catch { }
        return null;
    }

    System.Diagnostics.Process StartGuestProcess(string name, string executable, params string[] commandArguments)
    {
        var args = new List<string>
        {
            "--link2symlink", "-0", "-r", RootfsDirectory,
            "-b", "/dev", "-b", "/proc", "-b", "/sys",
            "-b", "/system", "-b", "/apex"
        };
        if (!IsNativeX64)
            args.AddRange(new[] { "-b", Box64Path + ":/usr/local/bin/box64" });

        args.AddRange(new[]
        {
            "-b", Path.Combine(RuntimeDirectory, "original") + ":/opt/uabea",
            "-b", HomeDirectory + ":/home/uabea",
            "-w", "/home/uabea"
        });
        if (!IsNativeX64)
            args.Add("/usr/local/bin/box64");
        args.Add(executable);
        args.AddRange(commandArguments);

        var start = new ProcessStartInfo(ProotPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = FilesDir!.AbsolutePath
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["PROOT_LOADER"] = LoaderPath;
        start.Environment["PROOT_TMP_DIR"] = Path.Combine(RuntimeDirectory, "proot-tmp");
        start.Environment["BOX64_PATH"] = "/usr/bin:/bin";
        start.Environment["DISPLAY"] = ":1";
        start.Environment["HOME"] = "/home/uabea";
        start.Environment["DOTNET_ROOT"] = "/opt/dotnet";
        start.Environment["LANG"] = "C.UTF-8";
        if (!IsNativeX64)
        {
            start.Environment["BOX64_LD_LIBRARY_PATH"] =
                "/lib/x86_64-linux-gnu:/usr/lib/x86_64-linux-gnu:/lib";
            // Use Box64's interpreter until ARM64 device testing confirms dynarec stability.
            start.Environment["BOX64_DYNAREC"] = "0";
            start.Environment["BOX64_LOG"] = "1";
            start.Environment["BOX64_SHOWSEGV"] = "1";
        }

        var logPath = Path.Combine(RuntimeDirectory, "logs", name + ".log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var log = new StreamWriter(logPath, append: true) { AutoFlush = true };
        log.WriteLine($"[{DateTime.UtcNow:O}] Launching: {ProotPath} {string.Join(" ", args)}");
        WriteRuntimeLog("PROCESS", $"Launching {name}; executable={ProotPath}; args={string.Join(" ", args)}; nativeHost={RuntimeInformation.ProcessArchitecture}; log={logPath}.");
        var process = new System.Diagnostics.Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        process.Exited += (_, _) =>
        {
            try
            {
                log.WriteLine($"[{DateTime.UtcNow:O}] Process exited. ExitCode={process.ExitCode}");
                log.Dispose();
            }
            catch { }
            WriteRuntimeLog("PROCESS", $"{name} exited. ExitCode={(process.HasExited ? process.ExitCode : -1)}.");
        };

        try
        {
            if (!process.Start())
                throw new IOException("Android refused to execute " + ProotPath + ".");
        }
        catch (Exception ex)
        {
            log.WriteLine("Process launch failed: " + ex);
            WriteRuntimeLog("PROCESS-ERROR", $"{name} launch failed: {ex}");
            log.Dispose();
            throw new IOException(
                "Could not execute Android native PRoot at " + ProotPath +
                ". Confirm the APK extracted the correct native PRoot and loader. " + ex.Message, ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (_runtimeProcesses.TryGetValue(name, out var previous))
        {
            try { previous.Dispose(); } catch { }
        }
        _runtimeProcesses[name] = process;
        WriteRuntimeLog("PROCESS", $"{name} started. PID={process.Id}.");
        return process;
    }

    async void RunBundleRoundTripSelfTest()
    {
        try
        {
            if (_installTask is not null && !await _installTask)
                throw new InvalidOperationException("Linux runtime installation failed; bundle self-test cannot run.");

            var testDirectory = Path.Combine(HomeDirectory, ".uabea-selftest");
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
            Directory.CreateDirectory(testDirectory);

            var bundlePath = Path.Combine(testDirectory, "smoke.unity3d");
            var entryPath = bundlePath + "_payload.txt";
            CreateUnityFsTestBundle(bundlePath);
            await File.WriteAllTextAsync(entryPath, "after");

            var beforeHash = SHA256.HashData(await File.ReadAllBytesAsync(bundlePath));
            _status.Text = "Testing original UABEA bundle edit and save…";

            var import = StartGuestProcess("UABEA-Bundle-Import",
                "/opt/dotnet/dotnet", "/opt/uabea/UABEAvalonia.dll",
                "batchimportbundle", "/home/uabea/.uabea-selftest");
            await import.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            if (import.ExitCode != 0)
                throw new IOException("UABEA bundle import/save command exited with code " + import.ExitCode + ".");

            var afterHash = SHA256.HashData(await File.ReadAllBytesAsync(bundlePath));
            if (CryptographicOperations.FixedTimeEquals(beforeHash, afterHash))
                throw new InvalidDataException("The test bundle did not change after the edit operation.");

            var export = StartGuestProcess("UABEA-Bundle-Verify",
                "/opt/dotnet/dotnet", "/opt/uabea/UABEAvalonia.dll",
                "batchexportbundle", "/home/uabea/.uabea-selftest");
            await export.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            if (export.ExitCode != 0)
                throw new IOException("UABEA bundle verification command exited with code " + export.ExitCode + ".");

            var roundTripText = (await File.ReadAllTextAsync(entryPath)).Trim();
            if (!string.Equals(roundTripText, "after", StringComparison.Ordinal))
                throw new InvalidDataException("The edited bundle entry did not survive reopening and export.");

            var savedBundle = await File.ReadAllBytesAsync(bundlePath);
            if (savedBundle.Length < 32 ||
                !savedBundle.AsSpan(0, 7).SequenceEqual(Encoding.ASCII.GetBytes("UnityFS")))
                throw new InvalidDataException("The saved bundle has an invalid UnityFS signature.");

            Directory.Delete(testDirectory, recursive: true);
            _status.Text = "UABEA bundle open/edit/save test passed.";
        }
        catch (Exception ex)
        {
            _status.Text = "Bundle self-test failed: " + ex.Message;
        }
    }

    static void CreateUnityFsTestBundle(string path)
    {
        var payload = Encoding.UTF8.GetBytes("before");
        var entryName = Encoding.UTF8.GetBytes("payload.txt\0");

        using var info = new MemoryStream();
        info.Write(new byte[16]);
        WriteUInt32BigEndian(info, 1);
        WriteUInt32BigEndian(info, (uint)payload.Length);
        WriteUInt32BigEndian(info, (uint)payload.Length);
        WriteUInt16BigEndian(info, 0x40);
        WriteUInt32BigEndian(info, 1);
        WriteUInt64BigEndian(info, 0);
        WriteUInt64BigEndian(info, (ulong)payload.Length);
        WriteUInt32BigEndian(info, 0);
        info.Write(entryName);
        var infoBytes = info.ToArray();

        var signature = Encoding.ASCII.GetBytes("UnityFS\0");
        var generation = Encoding.ASCII.GetBytes("5.x.x\0");
        var engine = Encoding.ASCII.GetBytes("2020.3.0f1\0");
        var headerLength = signature.Length + sizeof(uint) + generation.Length + engine.Length +
                           sizeof(ulong) + sizeof(uint) * 3;
        var totalLength = (ulong)(headerLength + infoBytes.Length + payload.Length);

        using var output = File.Create(path);
        output.Write(signature);
        WriteUInt32BigEndian(output, 6);
        output.Write(generation);
        output.Write(engine);
        WriteUInt64BigEndian(output, totalLength);
        WriteUInt32BigEndian(output, (uint)infoBytes.Length);
        WriteUInt32BigEndian(output, (uint)infoBytes.Length);
        WriteUInt32BigEndian(output, 0x40);
        output.Write(infoBytes);
        output.Write(payload);
    }

    static void WriteUInt16BigEndian(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    static void WriteUInt32BigEndian(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    static void WriteUInt64BigEndian(Stream stream, ulong value)
    {
        for (var shift = 56; shift >= 0; shift -= 8)
            stream.WriteByte((byte)(value >> shift));
    }

    static async Task<bool> WaitForFileAsync(string path, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(path)) return true;
            await Task.Delay(250);
        }
        return false;
    }

    static async Task<bool> WaitForPortAsync(int port, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                return true;
            }
            catch { await Task.Delay(300); }
        }
        return false;
    }

    void StopRuntimeFromUi()
    {
        StopRuntimeProcesses();
        _status.Text = "Runtime stopped.";
    }

    void StopRuntimeProcesses()
    {
        foreach (var process in _runtimeProcesses.Values.Reverse())
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { process.Dispose(); } catch { }
        }
        _runtimeProcesses.Clear();
        _startingRuntime = false;
    }

    void CopyVncAddress()
    {
        var clipboard = (Android.Content.ClipboardManager?)GetSystemService(ClipboardService);
        if (clipboard is not null) clipboard.PrimaryClip = ClipData.NewPlainText("VNC address", "127.0.0.1:5900");
        _status.Text = "Copied: 127.0.0.1:5900";
    }

    void PickUnityFile()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission |
                        ActivityFlags.GrantWriteUriPermission |
                        ActivityFlags.GrantPersistableUriPermission);
        StartActivityForResult(intent, PickFileRequest);
    }

    void PickOutputFolder()
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission |
                        ActivityFlags.GrantWriteUriPermission |
                        ActivityFlags.GrantPersistableUriPermission);
        StartActivityForResult(intent, OutputFolderRequest);
    }

    void StartHomeFileWatcher()
    {
        if (_homeWatcher is not null) return;
        _homeWatcher = new FileSystemWatcher(HomeDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _homeWatcher.Changed += (_, _) => ScheduleOutputSync();
        _homeWatcher.Created += (_, _) => ScheduleOutputSync();
        _homeWatcher.Renamed += (_, _) => ScheduleOutputSync();
    }

    void ScheduleOutputSync()
    {
        if (_outputTreeUri is null) return;
        _syncDebounce?.Cancel();
        _syncDebounce?.Dispose();
        _syncDebounce = new CancellationTokenSource();
        var token = _syncDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1800, token);
                await SyncHomeToOutputFolderAsync(token);
            }
            catch (System.OperationCanceledException) { }
            catch (Exception ex) { RunOnUiThread(() => _status.Text = "Output sync failed: " + ex.Message); }
        }, token);
    }

    async Task SyncHomeToOutputFolderAsync(CancellationToken cancellationToken)
    {
        if (_outputTreeUri is null) return;
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            var files = Directory.EnumerateFiles(HomeDirectory, "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .Where(path => !Path.GetRelativePath(HomeDirectory, path).StartsWith(
                    ".uabea-selftest", StringComparison.Ordinal))
                .Where(path => !Path.GetRelativePath(HomeDirectory, path).Contains(
                    Path.DirectorySeparatorChar + ".uabea-selftest", StringComparison.Ordinal))
                .ToArray();
            var synced = 0;
            var skipped = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stableStream = await OpenStableFileAsync(file, cancellationToken);
                if (stableStream is null)
                {
                    skipped++;
                    WriteRuntimeLog("SYNC-SKIP", $"Skipped a file that is still changing or locked: {file}");
                    continue;
                }

                using (stableStream)
                {
                    var relative = Path.GetRelativePath(HomeDirectory, file);
                    await CopyFileToTreeAsync(relative, _outputTreeUri, stableStream);
                    synced++;
                }
            }
            RunOnUiThread(() => _status.Text =
                $"Output sync complete: {synced} files copied; {skipped} changing files deferred.");
        }
        finally { _syncLock.Release(); }
    }

    async Task<FileStream?> OpenStableFileAsync(string path, CancellationToken cancellationToken)
    {
        long previousLength = -1;
        DateTime previousWrite = DateTime.MinValue;
        var stableSamples = 0;

        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return null;

            var info = new FileInfo(path);
            var length = info.Length;
            var lastWrite = info.LastWriteTimeUtc;
            if (length == previousLength && lastWrite == previousWrite)
                stableSamples++;
            else
                stableSamples = 0;

            previousLength = length;
            previousWrite = lastWrite;

            if (stableSamples >= 2)
            {
                try
                {
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (stream.Length == length && File.GetLastWriteTimeUtc(path) == lastWrite)
                    {
                        stream.Position = 0;
                        return stream;
                    }
                    stream.Dispose();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            await Task.Delay(500, cancellationToken);
        }

        return null;
    }

    async Task CopyFileToTreeAsync(
        string relativePath,
        Android.Net.Uri treeUri,
        FileStream input)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new IOException("Invalid empty output path.");
        var parent = Android.Provider.DocumentsContract.BuildDocumentUriUsingTree(
            treeUri, Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri));
        for (var i = 0; i < parts.Length - 1; i++)
            parent = FindOrCreateDirectory(treeUri, parent, parts[i]);

        var name = parts[^1];
        var tempName = $".{name}.uabea-tmp-{Guid.NewGuid():N}";
        var backupName = $".{name}.uabea-backup-{Guid.NewGuid():N}";
        var temp = Android.Provider.DocumentsContract.CreateDocument(
            ContentResolver!, parent, "application/octet-stream", tempName);
        if (temp is null) throw new IOException("Could not create temporary output file for " + name);

        Android.Net.Uri? backup = null;
        var committed = false;
        try
        {
            input.Position = 0;
            var sourceHash = await SHA256.HashDataAsync(input);
            input.Position = 0;
            using (var output = ContentResolver!.OpenOutputStream(temp, "w"))
            {
                if (output is null) throw new IOException("Could not write temporary output file for " + name);
                await input.CopyToAsync(output);
                await output.FlushAsync();
            }

            input.Position = 0;
            using (var verify = ContentResolver!.OpenInputStream(temp))
            {
                if (verify is null) throw new IOException("Could not reopen temporary output file for verification: " + name);
                var outputHash = await SHA256.HashDataAsync(verify);
                if (!CryptographicOperations.FixedTimeEquals(sourceHash, outputHash))
                    throw new InvalidDataException("Output verification failed; existing copy was preserved: " + name);
            }

            var target = FindChildDocument(treeUri, parent, name);
            if (target is not null)
            {
                backup = Android.Provider.DocumentsContract.RenameDocument(
                    ContentResolver!, target, backupName);
                if (backup is null)
                    throw new IOException("The selected output provider cannot safely rename the existing file. Existing copy was preserved: " + name);
            }

            var finalUri = Android.Provider.DocumentsContract.RenameDocument(
                ContentResolver!, temp, name);
            if (finalUri is null)
                throw new IOException("The selected output provider cannot safely commit the new file. Existing copy was preserved: " + name);

            committed = true;
            if (backup is not null)
            {
                try { Android.Provider.DocumentsContract.DeleteDocument(ContentResolver!, backup); }
                catch (Exception cleanupEx)
                {
                    WriteRuntimeLog("SYNC-WARN", $"New file saved but backup cleanup failed for '{name}': {cleanupEx.Message}");
                }
            }
        }
        catch
        {
            if (!committed && backup is not null)
            {
                try
                {
                    var restored = Android.Provider.DocumentsContract.RenameDocument(
                        ContentResolver!, backup, name);
                    if (restored is null)
                        WriteRuntimeLog("SYNC-CRITICAL", $"Could not restore previous output after failed replacement: {name}; backup={backup}.");
                }
                catch (Exception restoreEx)
                {
                    WriteRuntimeLog("SYNC-CRITICAL", $"Failed to restore previous output '{name}': {restoreEx}");
                }
            }
            if (!committed)
            {
                try { Android.Provider.DocumentsContract.DeleteDocument(ContentResolver!, temp); } catch { }
            }
            throw;
        }
    }

    Android.Net.Uri FindOrCreateDirectory(Android.Net.Uri treeUri, Android.Net.Uri parent, string name)
    {
        var existing = FindChildDocument(treeUri, parent, name);
        if (existing is not null) return existing;
        var created = Android.Provider.DocumentsContract.CreateDocument(
            ContentResolver!, parent, Android.Provider.DocumentsContract.Document.MimeTypeDir, name);
        return created ?? throw new IOException("Could not create output directory " + name);
    }

    Android.Net.Uri? FindChildDocument(Android.Net.Uri treeUri, Android.Net.Uri parent, string name)
    {
        var parentId = Android.Provider.DocumentsContract.GetDocumentId(parent);
        var children = Android.Provider.DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, parentId);
        using var cursor = ContentResolver!.Query(children,
            new[] { Android.Provider.DocumentsContract.Document.ColumnDocumentId,
                    Android.Provider.DocumentsContract.Document.ColumnDisplayName },
            null, null, null);
        if (cursor is null) return null;
        var idIndex = cursor.GetColumnIndex(Android.Provider.DocumentsContract.Document.ColumnDocumentId);
        var nameIndex = cursor.GetColumnIndex(Android.Provider.DocumentsContract.Document.ColumnDisplayName);
        while (cursor.MoveToNext())
        {
            if (nameIndex >= 0 && string.Equals(cursor.GetString(nameIndex), name, StringComparison.Ordinal))
            {
                var id = cursor.GetString(idIndex);
                return Android.Provider.DocumentsContract.BuildDocumentUriUsingTree(treeUri, id);
            }
        }
        return null;
    }

    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data is null) return;

        try
        {
            if (requestCode == OutputFolderRequest)
            {
                var flags = data.Flags &
                    (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                ContentResolver!.TakePersistableUriPermission(data.Data, flags);
                _outputTreeUri = data.Data;
                GetSharedPreferences("uabea-host", FileCreationMode.Private)!
                    .Edit()!.PutString("output_tree_uri", data.Data.ToString())!.Apply();
                _status.Text = "Output folder selected. Linux saves will sync there automatically.";
                await SyncHomeToOutputFolderAsync(CancellationToken.None);
                return;
            }

            if (requestCode != PickFileRequest) return;

            try
            {
                var persistFlags = data.Flags &
                    (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                ContentResolver!.TakePersistableUriPermission(data.Data, persistFlags);
            }
            catch { }

            Directory.CreateDirectory(HomeDirectory);
            var displayName = QueryDisplayName(data.Data) ?? "unity-file";
            var safeName = string.Concat(displayName.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var targetPath = Path.Combine(HomeDirectory, safeName);
            using var input = ContentResolver!.OpenInputStream(data.Data);
            if (input is null) throw new IOException("Android did not provide a readable file stream.");
            using (var output = File.Create(targetPath))
                await input.CopyToAsync(output);

            _lastImportedFile = targetPath;
            _status.Text = "Copied to Linux home: /home/uabea/" + safeName +
                ". Open this path from UABEA's File > Open dialog.";
        }
        catch (Exception ex)
        {
            _status.Text = "File operation failed: " + ex.Message;
        }
    }

    string? QueryDisplayName(Android.Net.Uri uri)
    {
        using var cursor = ContentResolver!.Query(uri, null, null, null, null);
        if (cursor is null || !cursor.MoveToFirst()) return null;
        var index = cursor.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
        return index >= 0 ? cursor.GetString(index) : null;
    }

    protected override void OnDestroy()
    {
        _homeWatcher?.Dispose();
        _syncDebounce?.Cancel();
        StopRuntimeProcesses();
        base.OnDestroy();
    }

    public override void OnBackPressed()
    {
        _homeWatcher?.Dispose();
        _syncDebounce?.Cancel();
        StopRuntimeProcesses();
        base.OnBackPressed();
    }
}
