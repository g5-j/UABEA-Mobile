using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UABEA.Mobile;

/// <summary>
/// Android-safe workspace model adapted from UABEA's AssetWorkspace.
/// Keeps edit identity, replacement payloads, removals and preview streams
/// separate from the Android presentation layer.
/// </summary>
public sealed class AssetWorkspace : IDisposable
{
    public AssetsManager AssetsManager { get; }
    public bool FromBundle { get; }
    public Dictionary<AssetID, AssetsReplacer> NewAssets { get; } = new();
    public Dictionary<AssetID, byte[]> NewAssetData { get; } = new();
    public Dictionary<AssetID, AssetsFileInstance> AssetFiles { get; } = new();
    public HashSet<AssetID> RemovedAssets { get; } = new();
    public bool Modified { get; private set; }

    public AssetWorkspace(AssetsManager assetsManager, bool fromBundle)
    {
        AssetsManager = assetsManager ?? throw new ArgumentNullException(nameof(assetsManager));
        FromBundle = fromBundle;
    }

    public void AddReplacer(AssetsFileInstance file, AssetsReplacer replacer, byte[]? previewData = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(replacer);

        var id = new AssetID(file.path, replacer.GetPathID());
        NewAssets[id] = replacer;
        AssetFiles[id] = file;

        if (previewData is null)
        {
            using var stream = new MemoryStream();
            using (var writer = new AssetsFileWriter(stream))
                replacer.Write(writer);
            previewData = stream.ToArray();
        }

        if (replacer is AssetsRemover)
        {
            NewAssetData.Remove(id);
            RemovedAssets.Add(id);
        }
        else
        {
            NewAssetData[id] = previewData;
            RemovedAssets.Remove(id);
        }

        Modified = true;
    }

    public bool RemoveReplacer(AssetsFileInstance file, long pathId)
    {
        var id = new AssetID(file.path, pathId);
        bool removed = NewAssets.Remove(id);
        AssetFiles.Remove(id);
        NewAssetData.Remove(id);
        RemovedAssets.Remove(id);
        Modified = NewAssets.Count != 0;
        return removed;
    }

    public bool TryGetPreviewData(AssetsFileInstance file, long pathId, out byte[] data) =>
        NewAssetData.TryGetValue(new AssetID(file.path, pathId), out data!);

    public List<AssetsReplacer> GetReplacersForFile(AssetsFileInstance file) =>
        NewAssets.Where(x => string.Equals(x.Key.fileName, file.path, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value).ToList();

    public List<AssetID> GetAssetIdsForFile(AssetsFileInstance file) =>
        NewAssets.Keys.Where(x => string.Equals(x.fileName, file.path, StringComparison.OrdinalIgnoreCase)).ToList();

    public HashSet<AssetsFileInstance> GetChangedFiles() =>
        AssetFiles.Values.ToHashSet();

    public void MarkSaved(IEnumerable<AssetID> ids)
    {
        foreach (var id in ids)
        {
            NewAssets.Remove(id);
            AssetFiles.Remove(id);
            NewAssetData.Remove(id);
            RemovedAssets.Remove(id);
        }
        Modified = NewAssets.Count != 0;
    }

    public void Reset()
    {
        NewAssets.Clear();
        AssetFiles.Clear();
        NewAssetData.Clear();
        RemovedAssets.Clear();
        Modified = false;
    }

    public void Dispose() => Reset();
}
