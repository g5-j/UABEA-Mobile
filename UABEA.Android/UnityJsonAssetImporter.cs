using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AssetsTools.NET;

namespace UABEA.Mobile;

internal static class UnityJsonAssetImporter
{
    public static byte[] Import(AssetTypeTemplateField template, string json, bool bigEndian)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        using MemoryStream stream = new();
        using AssetsFileWriter writer = new(stream);
        writer.BigEndian = bigEndian;
        WriteField(writer, template, document.RootElement);
        return stream.ToArray();
    }

    private static void WriteField(AssetsFileWriter writer, AssetTypeTemplateField template, JsonElement value)
    {
        bool align = template.IsAligned;

        if (template.IsArray)
        {
            if (template.ValueType == AssetValueType.ByteArray)
            {
                if (value.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"Field '{template.Name}' must be a JSON byte array.");
                if (value.GetArrayLength() > 128 * 1024 * 1024)
                    throw new InvalidDataException($"Field '{template.Name}' exceeds the 128 MB safety limit.");
                byte[] bytes = value.EnumerateArray().Select(x =>
                {
                    if (!x.TryGetByte(out byte b))
                        throw new InvalidDataException($"Field '{template.Name}' contains a value outside the byte range.");
                    return b;
                }).ToArray();
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
            else
            {
                if (value.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"Field '{template.Name}' must be a JSON array.");
                if (template.Children.Count < 2)
                    throw new InvalidDataException($"Array template '{template.Name}' is incomplete.");
                int count = value.GetArrayLength();
                if (count > 2_000_000)
                    throw new InvalidDataException($"Array '{template.Name}' exceeds the 2,000,000 element safety limit.");
                writer.Write(count);
                AssetTypeTemplateField elementTemplate = template.Children[1];
                foreach (JsonElement element in value.EnumerateArray())
                    WriteField(writer, elementTemplate, element);
            }

            if (align) writer.Align();
            return;
        }

        if (!template.HasValue)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Field '{template.Name}' must be a JSON object.");

            foreach (AssetTypeTemplateField child in template.Children)
            {
                if (!value.TryGetProperty(child.Name, out JsonElement childValue))
                    throw new InvalidDataException($"Missing field '{child.Name}' inside '{template.Name}'.");
                WriteField(writer, child, childValue);
            }

            if (align) writer.Align();
            return;
        }

        switch (template.ValueType)
        {
            case AssetValueType.Bool: writer.Write(value.GetBoolean()); break;
            case AssetValueType.Int8: writer.Write(value.GetSByte()); break;
            case AssetValueType.UInt8: writer.Write(value.GetByte()); break;
            case AssetValueType.Int16: writer.Write(value.GetInt16()); break;
            case AssetValueType.UInt16: writer.Write(value.GetUInt16()); break;
            case AssetValueType.Int32: writer.Write(value.GetInt32()); break;
            case AssetValueType.UInt32: writer.Write(value.GetUInt32()); break;
            case AssetValueType.Int64: writer.Write(value.GetInt64()); break;
            case AssetValueType.UInt64: writer.Write(value.GetUInt64()); break;
            case AssetValueType.Float: writer.Write(value.GetSingle()); break;
            case AssetValueType.Double: writer.Write(value.GetDouble()); break;
            case AssetValueType.String:
                writer.WriteCountStringInt32(value.GetString() ?? string.Empty);
                align = true;
                break;
            case AssetValueType.ManagedReferencesRegistry:
                throw new NotSupportedException("JSON import for SerializeReference / managed-reference registries is not supported. The asset was not changed.");
            default:
                throw new NotSupportedException($"Field '{template.Name}' uses unsupported serialized value type '{template.ValueType}'.");
        }

        if (align) writer.Align();
    }
}
