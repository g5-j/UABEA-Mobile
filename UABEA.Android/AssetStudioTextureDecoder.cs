using System.Runtime.InteropServices;
using AssetsTools.NET.Texture;

namespace UABEA.Mobile;

public static class AssetStudioTextureDecoder
{
    private const string LibraryName = "Texture2DDecoderNative";

    [DllImport(LibraryName, EntryPoint = "DecodeETC1", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeEtc1(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeETC2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeEtc2(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeETC2A1", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeEtc2A1(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeETC2A8", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeEtc2A8(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodePVRTC", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodePvrtc(byte[] data, int width, int height, byte[] output, int is2Bpp);

    [DllImport(LibraryName, EntryPoint = "DecodeDXT1", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeDxt1(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeDXT5", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeDxt5(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeEACR", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeEacR(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeEACRSigned", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeEacRSigned(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeEACRG", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeEacRg(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeEACRGSigned", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeEacRgSigned(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeBC4", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeBc4(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeBC5", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeBc5(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeBC6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeBc6(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeBC7", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeBc7(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeATCRGB4", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeAtcRgb4(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeATCRGBA8", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeAtcRgba8(byte[] data, int width, int height, byte[] output);

    [DllImport(LibraryName, EntryPoint = "DecodeASTC", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DecodeAstc(byte[] data, int width, int height, int blockWidth, int blockHeight, byte[] output);

    public static byte[]? TryDecode(byte[] data, TextureFormat format, int width, int height)
    {
        if (data == null || width <= 0 || height <= 0)
            return null;

        byte[] output = new byte[checked(width * height * 4)];
        int success;

        switch (format)
        {
            case TextureFormat.ETC_RGB4:
            case TextureFormat.ETC_RGB4_3DS:
                success = DecodeEtc1(data, width, height, output);
                break;
            case TextureFormat.ETC2_RGB4:
                success = DecodeEtc2(data, width, height, output);
                break;
            case TextureFormat.ETC2_RGBA1:
                success = DecodeEtc2A1(data, width, height, output);
                break;
            case TextureFormat.ETC2_RGBA8:
            case TextureFormat.ETC_RGBA8_3DS:
                success = DecodeEtc2A8(data, width, height, output);
                break;
            case TextureFormat.PVRTC_RGB2:
            case TextureFormat.PVRTC_RGBA2:
                success = DecodePvrtc(data, width, height, output, 1);
                break;
            case TextureFormat.PVRTC_RGB4:
            case TextureFormat.PVRTC_RGBA4:
                success = DecodePvrtc(data, width, height, output, 0);
                break;
            case TextureFormat.DXT1:
                success = DecodeDxt1(data, width, height, output);
                break;
            case TextureFormat.DXT5:
                success = DecodeDxt5(data, width, height, output);
                break;
            case TextureFormat.EAC_R:
                success = DecodeEacR(data, width, height, output);
                break;
            case TextureFormat.EAC_R_SIGNED:
                success = DecodeEacRSigned(data, width, height, output);
                break;
            case TextureFormat.EAC_RG:
                success = DecodeEacRg(data, width, height, output);
                break;
            case TextureFormat.EAC_RG_SIGNED:
                success = DecodeEacRgSigned(data, width, height, output);
                break;
            case TextureFormat.BC4:
                success = DecodeBc4(data, width, height, output);
                break;
            case TextureFormat.BC5:
                success = DecodeBc5(data, width, height, output);
                break;
            case TextureFormat.BC6H:
                success = DecodeBc6(data, width, height, output);
                break;
            case TextureFormat.BC7:
                success = DecodeBc7(data, width, height, output);
                break;
            case TextureFormat.ATC_RGB4:
                success = DecodeAtcRgb4(data, width, height, output);
                break;
            case TextureFormat.ATC_RGBA8:
                success = DecodeAtcRgba8(data, width, height, output);
                break;
            default:
                int block = GetAstcBlockSize(format);
                if (block == 0)
                    return null;
                success = DecodeAstc(data, width, height, block, block, output);
                break;
        }

        return success != 0 ? output : null;
    }

    private static int GetAstcBlockSize(TextureFormat format) => format switch
    {
        TextureFormat.ASTC_RGB_4x4 or TextureFormat.ASTC_RGBA_4x4 => 4,
        TextureFormat.ASTC_RGB_5x5 or TextureFormat.ASTC_RGBA_5x5 => 5,
        TextureFormat.ASTC_RGB_6x6 or TextureFormat.ASTC_RGBA_6x6 => 6,
        TextureFormat.ASTC_RGB_8x8 or TextureFormat.ASTC_RGBA_8x8 => 8,
        TextureFormat.ASTC_RGB_10x10 or TextureFormat.ASTC_RGBA_10x10 => 10,
        TextureFormat.ASTC_RGB_12x12 or TextureFormat.ASTC_RGBA_12x12 => 12,
        _ => 0
    };
}
