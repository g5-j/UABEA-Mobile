using UABEA.Mobile;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
            throw new FileNotFoundException(
                $"The exact texture stream '{name}' is missing. Do not substitute another resource file. offset={offset}; size={size}");
        using var fs = File.OpenRead(full);
        if (offset > (ulong)fs.Length || size > (ulong)fs.Length - offset)
            throw new InvalidDataException($"Resource range invalid: {Path.GetFileName(full)} offset={offset} size={size} length={fs.Length}");
        fs.Position = checked((long)offset);
        data = new byte[checked((int)size)];
        fs.ReadExactly(data);
        source = $"{name}@{offset}+{size}";
    }
    return (texture, data, source);
}

static byte[] DecodeAndValidate(byte[] encoded, TextureFormat format, int width, int height)
{
    byte[] decoded = AssetStudioTextureDecoder.TryDecode(encoded, format, width, height)
        ?? TextureFile.DecodeManaged(encoded, format, width, height);
    int required = checked(width * height * 4);
    if (decoded == null || decoded.Length < required)
        throw new NotSupportedException($"Decoder returned {decoded?.Length ?? 0} bytes, expected {required}.");
    return decoded;
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

int textureCount = 0, decodedCount = 0, failedCount = 0, missingResourceCount = 0, spriteCount = 0, spriteTextureResolved = 0;
var report = new List<string> { "PASS AssetStudio ETC1 native decoder: known solid-white 4x4 block." };
foreach (var asset in instance.file.AssetInfos)
{
    int type = asset.GetTypeId(instance.file);
    if (type == (int)AssetClassID.Texture2D)
    {
        textureCount++;
        try
        {
            var (texture, data, source) = LoadTexture(manager, instance, asset, directory);
            if (texture.m_Width == 0 && texture.m_Height == 0)
            {
                missingResourceCount++;
                report.Add($"SKIP Texture2D id={asset.PathId}: Unity placeholder texture has 0x0 dimensions.");
                continue;
            }
            if (texture.m_Width <= 0 || texture.m_Height <= 0 || data.Length == 0)
                throw new InvalidDataException($"Invalid texture payload {texture.m_Width}x{texture.m_Height}, {data.Length} bytes.");
            _ = DecodeAndValidate(data, (TextureFormat)texture.m_TextureFormat, texture.m_Width, texture.m_Height);
            decodedCount++;
            report.Add($"OK Texture2D id={asset.PathId} {texture.m_Width}x{texture.m_Height} format={(TextureFormat)texture.m_TextureFormat} bytes={data.Length} source={source}");
        }
        catch (FileNotFoundException ex)
        {
            missingResourceCount++;
            report.Add($"SKIP Texture2D id={asset.PathId}: {ex.Message}");
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
            _ = DecodeAndValidate(data, (TextureFormat)texture.m_TextureFormat, texture.m_Width, texture.m_Height);
            spriteTextureResolved++;
            report.Add($"OK Sprite id={asset.PathId} texture={pathId} {texture.m_Width}x{texture.m_Height} source={source}");
        }
        catch (FileNotFoundException ex)
        {
            missingResourceCount++;
            report.Add($"SKIP Sprite id={asset.PathId}: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            missingResourceCount++;
            report.Add($"SKIP Sprite id={asset.PathId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            failedCount++;
            report.Add($"FAIL Sprite id={asset.PathId}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

int replacementRoundTrips = 0;
foreach (var candidate in instance.file.AssetInfos.Where(x => x.GetTypeId(instance.file) == (int)AssetClassID.Texture2D))
{
    try
    {
        var originalField = ReadTextureField(manager, instance, candidate);
        var originalTexture = TextureFile.ReadTextureFile(originalField);
        if (originalTexture.m_Width <= 0 || originalTexture.m_Height <= 0 ||
            originalTexture.m_Width > 4096 || originalTexture.m_Height > 4096) continue;

        using var replacementImage = new Image<Rgba32>(originalTexture.m_Width, originalTexture.m_Height,
            new Rgba32(64, 128, 192, 255));
        using var png = new MemoryStream();
        replacementImage.SaveAsPng(png);
        byte[] replacementData = TextureAssetImporter.CreateReplacementData(manager, instance, candidate, png.ToArray());
        var replacer = new AssetsReplacerFromMemory(candidate.PathId,
            candidate.GetTypeId(instance.file), instance.file.GetScriptIndex(candidate), replacementData);
        string roundTripPath = Path.Combine(output, "texture-replacement-roundtrip.assets");
        using (var outputStream = File.Create(roundTripPath))
        using (var writer = new AssetsFileWriter(outputStream))
            instance.file.Write(writer, 0, new List<AssetsReplacer> { replacer });

        var verifyManager = new AssetsManager();
        try
        {
            var verified = verifyManager.LoadAssetsFile(roundTripPath, false);
            verifyManager.LoadClassPackage(classDataPath);
            verifyManager.LoadClassDatabaseFromPackage(verified.file.Metadata.UnityVersion);
            var verifiedAsset = verified.file.AssetInfos.FirstOrDefault(x => x.PathId == candidate.PathId)
                ?? throw new InvalidDataException("Replaced Texture2D Path ID disappeared after save.");
            var verifiedField = ReadTextureField(verifyManager, verified, verifiedAsset);
            var verifiedTexture = TextureFile.ReadTextureFile(verifiedField);
            if (verifiedTexture.m_Width != originalTexture.m_Width ||
                verifiedTexture.m_Height != originalTexture.m_Height ||
                verifiedTexture.m_TextureFormat != originalTexture.m_TextureFormat ||
                verifiedTexture.pictureData == null || verifiedTexture.pictureData.Length == 0)
                throw new InvalidDataException("Reopened Texture2D metadata or image payload did not match the replacement.");
            _ = DecodeAndValidate(verifiedTexture.pictureData, (TextureFormat)verifiedTexture.m_TextureFormat,
                verifiedTexture.m_Width, verifiedTexture.m_Height);
        }
        finally
        {
            verifyManager.UnloadAllAssetsFiles(true);
            verifyManager.UnloadAllBundleFiles();
        }

        replacementRoundTrips++;
        report.Add($"PASS Texture2D replacement save/reopen round-trip id={candidate.PathId} format={(TextureFormat)originalTexture.m_TextureFormat}");
        break;
    }
    catch (Exception ex)
    {
        report.Add($"Texture2D replacement round-trip candidate id={candidate.PathId} not usable: {ex.GetType().Name}: {ex.Message}");
    }
}
report.Insert(1, $"SUMMARY textures={textureCount}, decoded={decodedCount}, missing-resource={missingResourceCount}, failed={failedCount}; sprites={spriteCount}, sourceTexturesDecoded={spriteTextureResolved}");
File.WriteAllLines(Path.Combine(output, "report.txt"), report);
if (replacementRoundTrips == 0)
    throw new InvalidDataException("No Texture2D in the fixture could complete an import, save, reopen and decode round-trip. See texture-preview-output/report.txt for every candidate failure.");
report.Add($"PASS Texture2D replacement round-trips={replacementRoundTrips}");
File.WriteAllLines(Path.Combine(output, "report.txt"), report);
Console.WriteLine($"UnityVersion={instance.file.Metadata.UnityVersion}");
Console.WriteLine($"Textures={textureCount}, decoded={decodedCount}, missing-resource={missingResourceCount}, failed={failedCount}");
Console.WriteLine($"Sprites={spriteCount}, sourceTexturesDecoded={spriteTextureResolved}");
foreach (var line in report) Console.WriteLine(line);
if (textureCount == 0 || failedCount > 0)
    Environment.ExitCode = 2;
