using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Webkit;
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
public sealed class MainActivity : Activity
{
    const int PickFileRequest = 4101;
    const int SaveFileRequest = 4102;
    const string VncUrl = "http://127.0.0.1:6080/vnc.html?autoconnect=true&resize=remote";

    TextView _status = null!;
    WebView _display = null!;
    string? _lastImportedFile;
    readonly List<Process> _runtimeProcesses = new();

    string RuntimeDirectory => Path.Combine(FilesDir!.AbsolutePath, "runtime");
    string ImportDirectory => Path.Combine(FilesDir!.AbsolutePath, "imports");
    string RootfsDirectory => Path.Combine(RuntimeDirectory, "rootfs");
    string BinDirectory => Path.Combine(RuntimeDirectory, "bin");

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildHostUi();
        _ = InstallRuntimeAssetsAsync();
    }

    void BuildHostUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(12, 12, 12, 12);

        var header = new TextView(this)
        {
            Text = "UABEA ORIGINAL",
            TextSize = 20,
            Gravity = GravityFlags.CenterVertical
        };
        root.AddView(header, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(48)));

        var actions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        AddAction(actions, "Start", StartOriginalRuntime);
        AddAction(actions, "Open file", PickUnityFile);
        AddAction(actions, "Save copy", PickSaveDestination);
        AddAction(actions, "Reconnect", ConnectDisplay);
        root.AddView(actions, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(52)));

        _status = new TextView(this)
        {
            Text = "Installing original UABEA and Linux runtime…",
            TextSize = 13
        };
        root.AddView(_status, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(54)));

        _display = new WebView(this);
        _display.Settings.JavaScriptEnabled = true;
        _display.Settings.DomStorageEnabled = true;
        _display.Settings.AllowFileAccess = false;
        _display.Settings.AllowContentAccess = false;
        _display.SetWebChromeClient(new WebChromeClient());
        _display.SetWebViewClient(new WebViewClient());
        root.AddView(_display, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1));

        SetContentView(root);
        _display.LoadData(
            "<html><body style='background:#111;color:#eee;font:16px sans-serif;padding:24px'>" +
            "<h3>Original UABEA</h3><p>Press Start to launch the original Linux desktop application.</p></body></html>",
            "text/html", "UTF-8");
    }

    void AddAction(LinearLayout row, string title, Action action)
    {
        var button = new Button(this) { Text = title, TextSize = 11 };
        button.Click += (_, _) => action();
        row.AddView(button, new LinearLayout.LayoutParams(0, Dp(48), 1));
    }

    int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + 0.5f);

    async Task InstallRuntimeAssetsAsync()
    {
        try
        {
            Directory.CreateDirectory(RuntimeDirectory);
            Directory.CreateDirectory(BinDirectory);
            Directory.CreateDirectory(ImportDirectory);
            Directory.CreateDirectory(Path.Combine(RuntimeDirectory, "logs"));
            Directory.CreateDirectory(Path.Combine(RuntimeDirectory, "proot-tmp"));

            var originalMarker = Path.Combine(RuntimeDirectory, "original", "UABEAvalonia.dll");
            if (!File.Exists(originalMarker))
                await ExtractTarGzAssetAsync("uabea-original.tar.gz", Path.Combine(RuntimeDirectory, "original"));

            var rootfsMarker = Path.Combine(RootfsDirectory, "opt", "dotnet", "dotnet");
            if (!File.Exists(rootfsMarker))
                await ExtractTarGzAssetAsync("linux-x64-rootfs.tar.gz", RootfsDirectory);

            CopyAssetIfMissing("proot", Path.Combine(BinDirectory, "proot"));
            CopyAssetIfMissing("loader", Path.Combine(BinDirectory, "loader"));

            var box64Source = Path.Combine(ApplicationInfo!.NativeLibraryDir!, "libbox64.so");
            var box64Target = Path.Combine(BinDirectory, "box64");
            if (!File.Exists(box64Target))
                File.Copy(box64Source, box64Target, overwrite: true);

            SetExecutable(Path.Combine(BinDirectory, "proot"));
            SetExecutable(Path.Combine(BinDirectory, "loader"));
            SetExecutable(box64Target);

            _status.Text = "Linux x64 rootfs, PRoot, Box64 and original UABEA are installed. Press Start.";
        }
        catch (Exception ex)
        {
            _status.Text = "Runtime installation failed: " + ex.Message;
        }
    }

    void CopyAssetIfMissing(string assetName, string target)
    {
        if (File.Exists(target)) return;
        using var source = Assets!.Open(assetName);
        using var output = File.Create(target);
        source.CopyTo(output);
    }

    static void SetExecutable(string path)
    {
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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
        if (_runtimeProcesses.Count > 0)
        {
            _status.Text = "UABEA runtime is already running.";
            ConnectDisplay();
            return;
        }

        try
        {
            var proot = Path.Combine(BinDirectory, "proot");
            var loader = Path.Combine(BinDirectory, "loader");
            var box64 = Path.Combine(BinDirectory, "box64");
            var original = Path.Combine(RuntimeDirectory, "original");

            foreach (var required in new[] {
                proot, loader, box64,
                Path.Combine(RootfsDirectory, "opt", "dotnet", "dotnet"),
                Path.Combine(original, "UABEAvalonia.dll")
            })
                if (!File.Exists(required))
                    throw new FileNotFoundException("Missing runtime component: " + required);

            _status.Text = "Starting Linux display server…";
            StartGuestProcess("Xvfb", "/usr/local/bin/box64", "/usr/bin/Xvfb",
                ":1", "-screen", "0", "1280x800x24", "-nolisten", "tcp");

            var socketReady = await WaitForFileAsync(
                Path.Combine(RootfsDirectory, "tmp", ".X11-unix", "X1"), 15000);
            if (!socketReady)
                throw new IOException("Xvfb did not create its display socket. Check runtime/logs/Xvfb.log.");

            _status.Text = "Starting VNC and touch display bridge…";
            StartGuestProcess("x11vnc", "/usr/local/bin/box64", "/usr/bin/x11vnc",
                "-display", ":1", "-forever", "-shared", "-nopw", "-rfbport", "5900");
            StartGuestProcess("websockify", "/usr/local/bin/box64", "/usr/bin/python3",
                "/usr/bin/websockify", "--web=/usr/share/novnc", "6080", "127.0.0.1:5900");

            var webReady = await WaitForPortAsync(6080, 15000);
            if (!webReady)
                throw new IOException("noVNC did not start on port 6080. Check runtime/logs/websockify.log.");

            _status.Text = "Launching the original UABEA desktop application…";
            StartGuestProcess("UABEA", "/usr/local/bin/box64", "/opt/dotnet/dotnet",
                "/opt/uabea/UABEAvalonia.dll");

            await Task.Delay(1200);
            ConnectDisplay();
            _status.Text = "Original UABEA launched. Open files from /workspace inside its file dialog.";
        }
        catch (Exception ex)
        {
            _status.Text = "Runtime start failed: " + ex.Message;
            StopRuntimeProcesses();
        }
    }

    void StartGuestProcess(string name, params string[] command)
    {
        var proot = Path.Combine(BinDirectory, "proot");
        var loader = Path.Combine(BinDirectory, "loader");
        var box64 = Path.Combine(BinDirectory, "box64");
        var args = new List<string>
        {
            "--link2symlink", "-0", "-r", RootfsDirectory,
            "-b", "/dev", "-b", "/proc", "-b", "/sys",
            "-b", "/system", "-b", "/apex",
            "-b", box64 + ":/usr/local/bin/box64",
            "-b", Path.Combine(RuntimeDirectory, "original") + ":/opt/uabea",
            "-b", ImportDirectory + ":/workspace",
            "-w", "/"
        };
        args.AddRange(command);

        var start = new ProcessStartInfo(proot)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["PROOT_LOADER"] = loader;
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
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        process.Exited += (_, _) => { try { log.Dispose(); } catch { } };
        if (!process.Start())
            throw new IOException("Could not start " + name + ".");
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

    void StopRuntimeProcesses()
    {
        foreach (var process in _runtimeProcesses.AsEnumerable().Reverse())
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { process.Dispose(); } catch { }
        }
        _runtimeProcesses.Clear();
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
            _status.Text = "File copied to /workspace/" + safeName +
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

    void ConnectDisplay()
    {
        _display.LoadUrl(VncUrl);
        _status.Text = "Connecting to the local noVNC display…";
    }

    protected override void OnDestroy()
    {
        StopRuntimeProcesses();
        _display?.Destroy();
        base.OnDestroy();
    }

    public override void OnBackPressed()
    {
        if (_display.CanGoBack()) _display.GoBack();
        else base.OnBackPressed();
    }
}
