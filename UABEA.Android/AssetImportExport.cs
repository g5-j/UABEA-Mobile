using System;
using System.IO;
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
}
