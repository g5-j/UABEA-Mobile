using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace UABEA.Mobile;

/// <summary>
/// Android-facing import/export adapter for the core asset workspace.
/// Provides raw serialized data and type-tree JSON dump paths without
/// depending on UABEA's desktop file dialogs or plugin UI.
/// </summary>
internal static class AssetImportExport
{
    public static byte[] ExportRaw(AssetsFileInstance instance, AssetFileInfo asset, byte[]? pendingData = null)
    {
        if (pendingData is not null)
            return (byte[])pendingData.Clone();

        long start = asset.GetAbsoluteByteStart(instance.file);
        using var source = new SegmentStream(instance.file.Reader.BaseStream, start, asset.ByteSize);
        using var output = new MemoryStream();
        source.CopyTo(output);
        return output.ToArray();
    }

    public static AssetsReplacerFromMemory ImportRaw(
        AssetsFileInstance instance, AssetFileInfo asset, byte[] serializedData)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(serializedData);
        if (serializedData.Length == 0)
            throw new InvalidDataException("Raw asset data cannot be empty.");

        return new AssetsReplacerFromMemory(
            asset.PathId,
            asset.GetTypeId(instance.file),
            instance.file.GetScriptIndex(asset),
            serializedData);
    }

    public static byte[] ImportTypeTreeDump(
        AssetTypeTemplateField template, string json, bool bigEndian)
    {
        return UnityJsonAssetImporter.Import(template, json, bigEndian);
    }

    public static byte[] ExportTypeTreeDump(AssetTypeValueField root)
    {
        ArgumentNullException.ThrowIfNull(root);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
            WriteJsonField(writer, root);
        return output.ToArray();
    }

    private static void WriteJsonField(Utf8JsonWriter writer, AssetTypeValueField field)
    {
        var template = field.TemplateField;
        if (template.IsArray)
        {
            writer.WriteStartArray();
            if (template.ValueType == AssetValueType.ByteArray)
            {
                foreach (byte value in field.AsByteArray)
                    writer.WriteNumberValue(value);
            }
            else
            {
                foreach (var child in field.Children)
                    WriteJsonField(writer, child);
            }
            writer.WriteEndArray();
            return;
        }

        if (field.Value is not null)
        {
            switch (field.Value.ValueType)
            {
                case AssetValueType.Bool: writer.WriteBooleanValue(field.AsBool); break;
                case AssetValueType.Int8: writer.WriteNumberValue(field.AsSByte); break;
                case AssetValueType.UInt8: writer.WriteNumberValue(field.AsByte); break;
                case AssetValueType.Int16: writer.WriteNumberValue(field.AsShort); break;
                case AssetValueType.UInt16: writer.WriteNumberValue(field.AsUShort); break;
                case AssetValueType.Int32: writer.WriteNumberValue(field.AsInt); break;
                case AssetValueType.UInt32: writer.WriteNumberValue(field.AsUInt); break;
                case AssetValueType.Int64: writer.WriteNumberValue(field.AsLong); break;
                case AssetValueType.UInt64: writer.WriteNumberValue(field.AsULong); break;
                case AssetValueType.Float: writer.WriteNumberValue(field.AsFloat); break;
                case AssetValueType.Double: writer.WriteNumberValue(field.AsDouble); break;
                case AssetValueType.String: writer.WriteStringValue(field.AsString); break;
                case AssetValueType.ManagedReferencesRegistry:
                    throw new NotSupportedException("JSON dump export for managed-reference registries is not supported yet.");
                default:
                    throw new NotSupportedException($"JSON dump export does not support '{field.Value.ValueType}'.");
            }
            return;
        }

        writer.WriteStartObject();
        foreach (var child in field.Children)
        {
            writer.WritePropertyName(child.FieldName);
            WriteJsonField(writer, child);
        }
        writer.WriteEndObject();
    }
}
