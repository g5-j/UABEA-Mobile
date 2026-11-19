using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using System.Diagnostics;
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
    const int VncPort = 5900;

    TextView _status = null!;
    TextView _connection = null!;
    string? _lastImportedFile;
    Task? _installTask;
    readonly List<System.Diagnostics.Process> _runtimeProcesses = new();

    string RuntimeDirectory => Path.Combine(FilesDir!.AbsolutePath, "runtime");
    string ImportDirectory => Path.Combine(FilesDir!.AbsolutePath, "imports");
    string RootfsDirectory => Path.Combine(RuntimeDirectory, "rootfs");
    string NativeDirectory => ApplicationInfo!.NativeLibraryDir!;

    string ProotPath => Path.Combine(NativeDirectory, "libproot.so");
    string LoaderPath => Path.Combine(NativeDirectory, "libloader.so");
    string Box64Path => Path.Combine(NativeDirectory, "libbox64.so");

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildHostUi();
        _installTask = InstallRuntimeAssetsAsync();
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
        AddAction(files, "Save copy", PickSaveDestination);
        root.AddView(files, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(52)));

        var help = new TextView(this)
        {
            Text = "Start the runtime, then open a VNC Viewer app on this phone and connect to 127.0.0.1:5900. The VNC server accepts local connections only.",
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

    async Task InstallRuntimeAssetsAsync()
    {
        try
        {
            Directory.CreateDirectory(RuntimeDirectory);
            Directory.CreateDirectory(ImportDirectory);
            Directory.CreateDirectory(Path.Combine(RuntimeDirectory, "logs"));
            Directory.CreateDirectory(Path.Combine(RuntimeDirectory, "proot-tmp"));

            var originalMarker = Path.Combine(RuntimeDirectory, "original", "UABEAvalonia.dll");
            if (!File.Exists(originalMarker))
                await ExtractTarGzAssetAsync("uabea-original.tar.gz", Path.Combine(RuntimeDirectory, "original"));

            var rootfsMarker = Path.Combine(RootfsDirectory, "opt", "dotnet", "dotnet");
            if (!File.Exists(rootfsMarker))
                await ExtractTarGzAssetAsync("linux-x64-rootfs.tar.gz", RootfsDirectory);

            foreach (var nativeBinary in new[] { ProotPath, LoaderPath, Box64Path })
            {
                if (!File.Exists(nativeBinary))
                    throw new FileNotFoundException(
                        "Android did not extract a required executable from the APK native libraries: " + nativeBinary);
            }

            _status.Text = "Runtime installed. Press Start, then connect with a VNC app.";
        }
        catch (Exception ex)
        {
            _status.Text = "Runtime installation failed: " + ex.Message;
        }
    }

    async Task ExtractTarGzAssetAsync(string assetName, string destinationRoot)
    {
        if (Directory.Exists(destinationRoot))
            Directory.Delete(destinationRoot, recursive: true);
        Directory.CreateDirectory(destinationRoot);
        var fullRoot = Path.GetFullPath(destinationRoot);

        using var input = Assets!.Open(assetName);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        TarEntry? entry;
        long extracted = 0;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            var relative = entry.Name.TrimStart('.', '/', '\\')
                .Replace('/', Path.DirectorySeparatorChar);
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
                var linkTarget = Path.GetFullPath(Path.Combine(fullRoot, entry.LinkName.TrimStart('.', '/', '\\')));
                if (File.Exists(linkTarget))
                    File.Copy(linkTarget, output, overwrite: true);
                continue;
            }

            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var target = File.Create(output);
                if (entry.DataStream is not null)
                    await entry.DataStream.CopyToAsync(target);
                try { File.SetUnixFileMode(output, entry.Mode); } catch { }
                extracted++;
                if (extracted % 3000 == 0)
                    _status.Text = "Installing Linux runtime… " + extracted + " files";
            }
        }
    }

    async void StartOriginalRuntime()
    {
        if (_runtimeProcesses.Any(p => { try { return !p.HasExited; } catch { return false; } }))
        {
            _status.Text = "UABEA is already running. Connect using your VNC app.";
            return;
        }

        try
        {
            if (_installTask is not null)
                await _installTask;

            foreach (var required in new[] {
                ProotPath, LoaderPath, Box64Path,
                Path.Combine(RootfsDirectory, "opt", "dotnet", "dotnet"),
                Path.Combine(RuntimeDirectory, "original", "UABEAvalonia.dll")
            })
                if (!File.Exists(required))
                    throw new FileNotFoundException("Missing runtime component: " + required);

            _status.Text = "Starting Linux desktop…";
            StartGuestProcess("Xvfb", "/usr/local/bin/box64", "/usr/bin/Xvfb",
                ":1", "-screen", "0", "1280x800x24", "-nolisten", "tcp");

            var socketReady = await WaitForFileAsync(
                Path.Combine(RootfsDirectory, "tmp", ".X11-unix", "X1"), 20000);
            if (!socketReady)
                throw new IOException("Xvfb did not create its display socket. Check runtime/logs/Xvfb.log.");

            _status.Text = "Starting local VNC server…";
            StartGuestProcess("x11vnc", "/usr/local/bin/box64", "/usr/bin/x11vnc",
                "-display", ":1", "-forever", "-shared", "-nopw",
                "-localhost", "-rfbport", VncPort.ToString());

            if (!await WaitForPortAsync(VncPort, 15000))
                throw new IOException("VNC server did not listen on 127.0.0.1:5900. Check runtime/logs/x11vnc.log.");

            _status.Text = "Launching original UABEA…";
            StartGuestProcess("UABEA", "/usr/local/bin/box64", "/opt/dotnet/dotnet",
                "/opt/uabea/UABEAvalonia.dll");

            await Task.Delay(1500);
            var app = _runtimeProcesses.LastOrDefault();
            if (app is null || app.HasExited)
                throw new IOException("UABEA exited during startup. Check runtime/logs/UABEA.log.");

            _status.Text = "UABEA is running. Connect your VNC app to 127.0.0.1:5900.";
        }
        catch (Exception ex)
        {
            _status.Text = "Runtime start failed: " + ex.Message;
            StopRuntimeProcesses();
        }
    }

    void StartGuestProcess(string name, params string[] command)
    {
        var args = new List<string>
        {
            "--link2symlink", "-0", "-r", RootfsDirectory,
            "-b", "/dev", "-b", "/proc", "-b", "/sys",
            "-b", "/system", "-b", "/apex",
            "-b", Box64Path + ":/usr/local/bin/box64",
            "-b", Path.Combine(RuntimeDirectory, "original") + ":/opt/uabea",
            "-b", ImportDirectory + ":/workspace",
            "-w", "/"
        };
        args.AddRange(command);

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
        start.Environment["BOX64_LD_LIBRARY_PATH"] =
            "/lib/x86_64-linux-gnu:/usr/lib/x86_64-linux-gnu:/lib";
        start.Environment["BOX64_PATH"] = "/usr/bin:/bin";
        start.Environment["DISPLAY"] = ":1";
        start.Environment["HOME"] = "/root";
        start.Environment["DOTNET_ROOT"] = "/opt/dotnet";
        start.Environment["LANG"] = "C.UTF-8";

        var logPath = Path.Combine(RuntimeDirectory, "logs", name + ".log");
        var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        var process = new System.Diagnostics.Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        process.Exited += (_, _) => { try { log.Dispose(); } catch { } };

        try
        {
            if (!process.Start())
                throw new IOException("Android refused to execute " + ProotPath + ".");
        }
        catch (Exception ex)
        {
            log.WriteLine("Process launch failed: " + ex);
            log.Dispose();
            throw new IOException(
                "Could not execute Android native PRoot at " + ProotPath +
                ". Confirm the APK extracted libproot.so and libloader.so. " + ex.Message, ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _runtimeProcesses.Add(process);
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
        foreach (var process in _runtimeProcesses.AsEnumerable().Reverse())
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { process.Dispose(); } catch { }
        }
        _runtimeProcesses.Clear();
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

    void PickSaveDestination()
    {
        if (string.IsNullOrEmpty(_lastImportedFile) || !File.Exists(_lastImportedFile))
        {
            _status.Text = "Open a Unity file first.";
            return;
        }

        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraTitle, Path.GetFileNameWithoutExtension(_lastImportedFile) + "-edited" +
            Path.GetExtension(_lastImportedFile));
        intent.AddFlags(ActivityFlags.GrantWriteUriPermission |
                        ActivityFlags.GrantReadUriPermission |
                        ActivityFlags.GrantPersistableUriPermission);
        StartActivityForResult(intent, SaveFileRequest);
    }

    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data is null) return;

        try
        {
            if (requestCode == SaveFileRequest)
            {
                if (string.IsNullOrEmpty(_lastImportedFile) || !File.Exists(_lastImportedFile))
                    throw new FileNotFoundException("The imported workspace file no longer exists.");
                using var source = File.OpenRead(_lastImportedFile);
                using var target = ContentResolver!.OpenOutputStream(data.Data, "wt");
                if (target is null) throw new IOException("Android did not provide a writable output stream.");
                await source.CopyToAsync(target);
                _status.Text = "Edited file exported to the selected phone location.";
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

            Directory.CreateDirectory(ImportDirectory);
            var displayName = QueryDisplayName(data.Data) ?? "unity-file";
            var safeName = string.Concat(displayName.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var targetPath = Path.Combine(ImportDirectory, safeName);
            using var input = ContentResolver!.OpenInputStream(data.Data);
            if (input is null) throw new IOException("Android did not provide a readable file stream.");
            using (var output = File.Create(targetPath))
                await input.CopyToAsync(output);

            _lastImportedFile = targetPath;
            _status.Text = "Copied to /workspace/" + safeName +
                ". In UABEA, use File > Open and select this path.";
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
        StopRuntimeProcesses();
        base.OnDestroy();
    }

    public override void OnBackPressed()
    {
        StopRuntimeProcesses();
        base.OnBackPressed();
    }
}
