using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using TexturePlugin;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace UABEA.Mobile;

public static class TextureAssetImporter
{
    public static byte[] CreateReplacementData(
        AssetsManager manager, AssetsFileInstance instance, AssetFileInfo asset, byte[] encodedImage)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(encodedImage);
        if (encodedImage.Length == 0) throw new InvalidDataException("The selected image is empty.");

        using Image<Rgba32> image = Image.Load<Rgba32>(encodedImage);
        int width = image.Width, height = image.Height;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 ||
            (long)width * height > 32_000_000)
            throw new InvalidDataException("Image dimensions exceed the safe import limit (8192 per side / 32 MP).");

        var template = manager.GetTemplateBaseField(instance, asset)
            ?? throw new InvalidDataException("Texture2D type information could not be read.");
        var imageTemplate = template.Children.FirstOrDefault(x => x.Name == "image data" || x.Name == "m_ImageData")
            ?? throw new InvalidDataException("Texture2D has no image data field.");
        imageTemplate.ValueType = AssetValueType.ByteArray;
        var blobTemplate = template.Children.FirstOrDefault(x => x.Name == "m_PlatformBlob");
        if (blobTemplate is not null && blobTemplate.Children.Count > 0)
            blobTemplate.Children[0].ValueType = AssetValueType.ByteArray;

        AssetTypeValueField field;
        var reader = instance.file.Reader;
        lock (reader) field = template.MakeValue(reader, asset.GetAbsoluteByteStart(instance.file), null);

        var texture = TextureFile.ReadTextureFile(field);
        TextureFormat format = (TextureFormat)texture.m_TextureFormat;
        byte[] platformBlob = GetPlatformBlob(field);
        uint platform = instance.file.Metadata.TargetPlatform;
        int mipCount = Math.Max(1, texture.m_MipCount);
        if (width != texture.m_Width || height != texture.m_Height)
        {
            mipCount = IsPowerOfTwo(width) && IsPowerOfTwo(height)
                ? Math.Max(System.Numerics.BitOperations.Log2((uint)width),
                    System.Numerics.BitOperations.Log2((uint)height)) + 1
                : 1;
        }

        byte[] imageData;
        if (platform == 38 && platformBlob.Length != 0)
        {
            TextureFormat encodeFormat = CorrectSwitchFormat(format);
            int gobsPerBlock = Texture2DSwitchDeswizzler.GetSwitchGobsPerBlock(platformBlob);
            Size blockSize = Texture2DSwitchDeswizzler.TextureFormatToBlockSize(encodeFormat);
            Size paddedSize = Texture2DSwitchDeswizzler.GetPaddedTextureSize(
                width, height, blockSize.Width, blockSize.Height, gobsPerBlock);
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Mode = ResizeMode.BoxPad,
                Position = AnchorPositionMode.BottomLeft,
                PadColor = Color.Fuchsia,
                Size = paddedSize
            }).Flip(FlipMode.Vertical));
            using Image<Rgba32> swizzled = Texture2DSwitchDeswizzler.SwitchSwizzle(image, blockSize, gobsPerBlock);
            imageData = TextureEncoderDecoder.Encode(swizzled, paddedSize.Width, paddedSize.Height, encodeFormat);
        }
        else
        {
            image.Mutate(ctx => ctx.Flip(FlipMode.Vertical));
            imageData = TextureEncoderDecoder.Encode(image, width, height, format, 5, mipCount);
        }

        if (imageData is null || imageData.Length == 0)
            throw new InvalidDataException($"Texture encoder returned no data for format {format}.");

        var streamData = field["m_StreamData"];
        if (!streamData.IsDummy)
        {
            streamData["offset"].AsInt = 0;
            streamData["size"].AsInt = 0;
            streamData["path"].AsString = "";
        }
        if (!field["m_MipCount"].IsDummy) field["m_MipCount"].AsInt = mipCount;
        field["m_CompleteImageSize"].AsInt = imageData.Length;
        field["m_Width"].AsInt = width;
        field["m_Height"].AsInt = height;

        var imageDataField = field["image data"];
        if (imageDataField.IsDummy) imageDataField = field["m_ImageData"];
        if (imageDataField.IsDummy) throw new InvalidDataException("Texture2D image data field could not be accessed.");
        imageDataField.Value.ValueType = AssetValueType.ByteArray;
        imageDataField.TemplateField.ValueType = AssetValueType.ByteArray;
        imageDataField.AsByteArray = imageData;
        return field.WriteToByteArray(reader.BigEndian);
    }

    private static byte[] GetPlatformBlob(AssetTypeValueField field)
    {
        var blob = field["m_PlatformBlob"];
        if (blob is null || blob.IsDummy || blob.Children.Count == 0) return Array.Empty<byte>();
        var array = blob["Array"];
        return array is null || array.IsDummy ? Array.Empty<byte>() : array.AsByteArray ?? Array.Empty<byte>();
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
    private static TextureFormat CorrectSwitchFormat(TextureFormat format) => format switch
    {
        TextureFormat.RGB24 => TextureFormat.RGBA32,
        TextureFormat.BGR24 => TextureFormat.BGRA32,
        _ => format
    };
}
