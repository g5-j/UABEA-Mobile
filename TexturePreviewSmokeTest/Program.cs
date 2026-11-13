using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.IO.Compression;

static AssetTypeValueField ReadTextureField(AssetsManager manager, AssetsFileInstance instance, AssetFileInfo asset)
{
    var template = manager.GetTemplateBaseField(instance, asset)
        ?? throw new InvalidDataException("No Texture2D template.");
    var imageData = template.Children.FirstOrDefault(x => x.Name == "image data");
    if (imageData != null) imageData.ValueType = AssetValueType.ByteArray;
    var platformBlob = template.Children.FirstOrDefault(x => x.Name == "m_PlatformBlob");
    if (platformBlob != null && platformBlob.Children.Count > 0)
        platformBlob.Children[0].ValueType = AssetValueType.ByteArray;
    var reader = instance.file.Reader;
    lock (reader)
        return template.MakeValue(reader, asset.GetAbsoluteByteStart(instance.file), null);
}

static ulong ReadUnsigned(AssetTypeValueField? field)
{
    if (field == null || field.IsDummy || field.Value == null) return 0;
    return field.Value.ValueType switch
    {
        AssetValueType.UInt8 => field.AsByte,
        AssetValueType.UInt16 => field.AsUShort,
        AssetValueType.UInt32 => field.AsUInt,
        AssetValueType.UInt64 => field.AsULong,
        AssetValueType.Int8 => checked((ulong)field.AsSByte),
        AssetValueType.Int16 => checked((ulong)field.AsShort),
        AssetValueType.Int32 => checked((ulong)field.AsInt),
        AssetValueType.Int64 => checked((ulong)field.AsLong),
        _ => throw new InvalidDataException($"Unexpected numeric field type: {field.Value.ValueType}.")
    };
}

static (TextureFile Texture, byte[] Data, string Source) LoadTexture(
    AssetsManager manager, AssetsFileInstance instance, AssetFileInfo asset, string directory)
{
    var field = ReadTextureField(manager, instance, asset);
    var texture = TextureFile.ReadTextureFile(field);
    byte[] data = texture.pictureData ?? Array.Empty<byte>();
    string source = "embedded";
    var stream = field["m_StreamData"];
    ulong offset = 0, size = 0;
    string path = "";
    if (stream != null && !stream.IsDummy)
    {
        var off = stream["offset"];
        var siz = stream["size"];
        var pth = stream["path"];
        offset = ReadUnsigned(off);
        size = ReadUnsigned(siz);
        if (pth != null && !pth.IsDummy) path = pth.AsString ?? "";
    }
    if (size > 0 && !string.IsNullOrWhiteSpace(path))
    {
        string name = Path.GetFileName(path.Replace("archive:/", "", StringComparison.OrdinalIgnoreCase));
        string full = Path.Combine(directory, name);
        if (!File.Exists(full))
            throw new FileNotFoundException($"External texture resource missing: {path}");
        using var fs = File.OpenRead(full);
        if (offset > (ulong)fs.Length || size > (ulong)fs.Length - offset)
            throw new InvalidDataException($"Resource range invalid: {name} offset={offset} size={size} length={fs.Length}");
        fs.Position = checked((long)offset);
        data = new byte[checked((int)size)];
        fs.ReadExactly(data);
        source = $"{name}@{offset}+{size}";
    }
    return (texture, data, source);
}

static void SavePreview(byte[] encoded, TextureFormat format, int width, int height, string path)
{
    byte[] bgra = TextureFile.DecodeManaged(encoded, format, width, height);
    int required = checked(width * height * 4);
    if (bgra == null || bgra.Length < required)
        throw new NotSupportedException($"Decode returned {bgra?.Length ?? 0} bytes, expected {required}.");
    var rgba = new byte[required];
    for (int i = 0; i < required; i += 4)
    {
        rgba[i] = bgra[i + 2];
        rgba[i + 1] = bgra[i + 1];
        rgba[i + 2] = bgra[i];
        rgba[i + 3] = bgra[i + 3];
    }
    using var image = Image.LoadPixelData<Rgba32>(rgba, width, height);
    image.Mutate(x => x.Flip(FlipMode.Vertical));
    image.SaveAsPng(path);
}

if (args.Length < 2) throw new ArgumentException("Usage: TexturePreviewSmokeTest <assets-file> <classdata.tpk>");
string assetsPath = Path.GetFullPath(args[0]);
string classDataPath = Path.GetFullPath(args[1]);
string directory = Path.GetDirectoryName(assetsPath)!;
string output = Path.Combine(Environment.CurrentDirectory, "texture-preview-output");
Directory.CreateDirectory(output);

var manager = new AssetsManager();
manager.LoadClassPackage(classDataPath);
var instance = manager.LoadAssetsFile(assetsPath, false);
manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);

int textureCount = 0, decodedCount = 0, failedCount = 0, spriteCount = 0, spriteTextureResolved = 0;
var report = new List<string>();
foreach (var asset in instance.file.AssetInfos)
{
    int type = asset.GetTypeId(instance.file);
    if (type == (int)AssetClassID.Texture2D)
    {
        textureCount++;
        try
        {
            var (texture, data, source) = LoadTexture(manager, instance, asset, directory);
            if (texture.m_Width <= 0 || texture.m_Height <= 0 || data.Length == 0)
                throw new InvalidDataException($"Invalid texture payload {texture.m_Width}x{texture.m_Height}, {data.Length} bytes.");
            string file = Path.Combine(output, $"texture-{asset.PathId}.png");
            SavePreview(data, (TextureFormat)texture.m_TextureFormat, texture.m_Width, texture.m_Height, file);
            decodedCount++;
            report.Add($"OK Texture2D id={asset.PathId} {texture.m_Width}x{texture.m_Height} format={(TextureFormat)texture.m_TextureFormat} bytes={data.Length} source={source} png={Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            failedCount++;
            report.Add($"FAIL Texture2D id={asset.PathId}: {ex.GetType().Name}: {ex.Message}");
        }
    }
    else if (type == (int)AssetClassID.Sprite)
    {
        spriteCount++;
        try
        {
            var sprite = manager.GetBaseField(instance, asset);
            var rd = sprite["m_RD"];
            var texPtr = rd["texture"];
            long pathId = texPtr["m_PathID"]?.AsLong ?? 0;
            var texAsset = instance.file.AssetInfos.FirstOrDefault(x => x.PathId == pathId && x.GetTypeId(instance.file) == (int)AssetClassID.Texture2D);
            if (texAsset == null) throw new InvalidDataException($"Sprite texture pointer {pathId} not found in this assets file.");
            var (texture, data, source) = LoadTexture(manager, instance, texAsset, directory);
            if (data.Length == 0) throw new InvalidDataException("Sprite source texture is empty.");
            string file = Path.Combine(output, $"sprite-source-{asset.PathId}.png");
            SavePreview(data, (TextureFormat)texture.m_TextureFormat, texture.m_Width, texture.m_Height, file);
            spriteTextureResolved++;
            report.Add($"OK Sprite id={asset.PathId} texture={pathId} {texture.m_Width}x{texture.m_Height} source={source} png={Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            report.Add($"FAIL Sprite id={asset.PathId}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
File.WriteAllLines(Path.Combine(output, "report.txt"), report);
Console.WriteLine($"UnityVersion={instance.file.Metadata.UnityVersion}");
Console.WriteLine($"Textures={textureCount}, decoded={decodedCount}, failed={failedCount}");
Console.WriteLine($"Sprites={spriteCount}, sourceTexturesDecoded={spriteTextureResolved}");
foreach (var line in report) Console.WriteLine(line);
if (textureCount == 0 || decodedCount == 0 || failedCount > 0)
    Environment.ExitCode = 2;
