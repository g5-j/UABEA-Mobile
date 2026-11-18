using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
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
        if (encodedImage.Length == 0)
            throw new InvalidDataException("The selected image is empty.");

        // Nintendo Switch textures require platform-specific GOB swizzling and metadata.
        // Never silently write a generic linear texture into a Switch asset.
        if (instance.file.Metadata.TargetPlatform == 38)
            throw new NotSupportedException("Nintendo Switch texture replacement is not supported by the Android importer.");

        using Image<Rgba32> image = Image.Load<Rgba32>(encodedImage);
        int width = image.Width;
        int height = image.Height;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 ||
            (long)width * height > 32_000_000)
            throw new InvalidDataException("Image dimensions exceed the safe import limit (8192 per side / 32 MP).");

        var template = manager.GetTemplateBaseField(instance, asset)
            ?? throw new InvalidDataException("Texture2D type information could not be read.");
        var imageTemplate = template.Children.FirstOrDefault(x =>
            x.Name == "image data" || x.Name == "m_ImageData")
            ?? throw new InvalidDataException("Texture2D has no image data field.");
        imageTemplate.ValueType = AssetValueType.ByteArray;

        var blobTemplate = template.Children.FirstOrDefault(x => x.Name == "m_PlatformBlob");
        if (blobTemplate is not null && blobTemplate.Children.Count > 0)
            blobTemplate.Children[0].ValueType = AssetValueType.ByteArray;

        AssetTypeValueField field;
        var reader = instance.file.Reader;
        lock (reader)
            field = template.MakeValue(reader, asset.GetAbsoluteByteStart(instance.file), null);

        // RGBA32 is encoded directly from managed pixel memory. This avoids the
        // unavailable textoolwrap native library and works on Android ARM64.
        image.Mutate(ctx => ctx.Flip(FlipMode.Vertical));
        byte[] imageData = new byte[checked(width * height * 4)];
        image.CopyPixelDataTo(imageData);

        var streamData = field["m_StreamData"];
        if (streamData is not null && !streamData.IsDummy)
        {
            streamData["offset"].AsInt = 0;
            streamData["size"].AsInt = 0;
            streamData["path"].AsString = "";
        }

        var platformBlob = field["m_PlatformBlob"];
        if (platformBlob is not null && !platformBlob.IsDummy)
        {
            var blobArray = platformBlob["Array"];
            if (blobArray is not null && !blobArray.IsDummy)
            {
                blobArray.Value.ValueType = AssetValueType.ByteArray;
                blobArray.TemplateField.ValueType = AssetValueType.ByteArray;
                blobArray.AsByteArray = Array.Empty<byte>();
            }
        }

        field["m_TextureFormat"].AsInt = (int)TextureFormat.RGBA32;
        if (!field["m_MipCount"].IsDummy)
            field["m_MipCount"].AsInt = 1;
        field["m_CompleteImageSize"].AsInt = imageData.Length;
        field["m_Width"].AsInt = width;
        field["m_Height"].AsInt = height;

        var imageDataField = field["image data"];
        if (imageDataField.IsDummy)
            imageDataField = field["m_ImageData"];
        if (imageDataField.IsDummy)
            throw new InvalidDataException("Texture2D image data field could not be accessed.");

        imageDataField.Value.ValueType = AssetValueType.ByteArray;
        imageDataField.TemplateField.ValueType = AssetValueType.ByteArray;
        imageDataField.AsByteArray = imageData;

        return field.WriteToByteArray(reader.BigEndian);
    }
}
