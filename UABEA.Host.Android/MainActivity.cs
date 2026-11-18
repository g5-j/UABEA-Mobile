using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using System.Formats.Tar;
using System.IO.Compression;

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
    const string VncUrl = "http://127.0.0.1:6080/vnc.html?autoconnect=true&resize=remote";

    TextView _status = null!;
    WebView _display = null!;
    string? _lastImportedFile;
    string RuntimeDirectory => Path.Combine(FilesDir!.AbsolutePath, "runtime");
    string ImportDirectory => Path.Combine(FilesDir!.AbsolutePath, "imports");

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildHostUi();
        _ = InstallOriginalPayloadAsync();
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
        AddAction(actions, "Reconnect", ConnectDisplay);
        root.AddView(actions, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(52)));

        _status = new TextView(this)
        {
            Text = "Preparing original UABEA payload…",
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
            "<h3>Original UABEA display</h3><p>The VNC display bridge will appear here after the Linux runtime starts.</p></body></html>",
            "text/html", "UTF-8");
    }

    void AddAction(LinearLayout row, string title, Action action)
    {
        var button = new Button(this) { Text = title };
        button.Click += (_, _) => action();
        row.AddView(button, new LinearLayout.LayoutParams(0, Dp(48), 1));
    }

    int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + 0.5f);

    async Task InstallOriginalPayloadAsync()
    {
        try
        {
            Directory.CreateDirectory(RuntimeDirectory);
            var marker = Path.Combine(RuntimeDirectory, "original", "UABEAvalonia.dll");
            if (File.Exists(marker))
            {
                _status.Text = "Original UABEA payload is installed. Runtime components are checked when Start is pressed.";
                return;
            }

            using var input = Assets!.Open("uabea-original.tar.gz");
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            var destinationRoot = Path.GetFullPath(Path.Combine(RuntimeDirectory, "original"));
            Directory.CreateDirectory(destinationRoot);

            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                var relative = entry.Name.Replace('/', Path.DirectorySeparatorChar);
                var output = Path.GetFullPath(Path.Combine(destinationRoot, relative));
                if (!output.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !string.Equals(output, destinationRoot, StringComparison.Ordinal))
                    throw new InvalidDataException("Unsafe path in UABEA runtime archive.");

                if (entry.EntryType is TarEntryType.Directory)
                {
                    Directory.CreateDirectory(output);
                    continue;
                }

                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    using var target = File.Create(output);
                    if (entry.DataStream is not null)
                        await entry.DataStream.CopyToAsync(target);
                }
            }

            _status.Text = "Original UABEA files installed. Box64, Linux rootfs and display server are not bundled yet.";
        }
        catch (Exception ex)
        {
            _status.Text = "Payload install failed: " + ex.Message;
        }
    }

    void StartOriginalRuntime()
    {
        var box64 = Path.Combine(FilesDir!.AbsolutePath, "runtime", "bin", "box64");
        var proot = Path.Combine(FilesDir.AbsolutePath, "runtime", "bin", "proot");
        var dotnet = Path.Combine(FilesDir.AbsolutePath, "runtime", "rootfs", "usr", "share", "dotnet", "dotnet");

        var missing = new List<string>();
        if (!File.Exists(box64)) missing.Add("Box64 ARM64 executable");
        if (!File.Exists(proot)) missing.Add("PRoot executable");
        if (!File.Exists(dotnet)) missing.Add("Linux x64 .NET 8 runtime");
        if (missing.Count > 0)
        {
            _status.Text = "Cannot start yet. Missing runtime components: " + string.Join(", ", missing) +
                ". The original UABEA payload is present; the Android Linux runtime is the next integration stage.";
            return;
        }

        _status.Text = "Runtime components detected. Runtime process and VNC server wiring is not enabled in this build.";
    }

    void PickUnityFile()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        StartActivityForResult(intent, PickFileRequest);
    }

    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickFileRequest || resultCode != Result.Ok || data?.Data is null)
            return;

        try
        {
            Directory.CreateDirectory(ImportDirectory);
            var displayName = QueryDisplayName(data.Data) ?? "unity-file";
            var safeName = string.Concat(displayName.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var target = Path.Combine(ImportDirectory, safeName);
            using var source = ContentResolver!.OpenInputStream(data.Data);
            if (source is null) throw new IOException("Android did not provide a readable file stream.");
            using (var output = File.Create(target))
                await source.CopyToAsync(output);

            _lastImportedFile = target;
            _status.Text = "Imported to app storage: " + target +
                ". The file bridge to the Linux workspace is not connected yet.";
        }
        catch (Exception ex)
        {
            _status.Text = "File import failed: " + ex.Message;
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
        _status.Text = "Connecting to local noVNC display. The display service must be running first.";
    }

    public override void OnBackPressed()
    {
        if (_display.CanGoBack()) _display.GoBack();
        else base.OnBackPressed();
    }
}
