using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace UABEA.Mobile;

/// <summary>
/// Internal adapter between the Android presentation layer and the original
/// UABEA workspace/file engine. All serialized file operations are performed
/// in-process by AssetsTools.NET; no desktop UI or operating-system emulation
/// is involved.
/// </summary>
internal sealed class UabeaEngineBridge : IDisposable
{
    private readonly UABEAvalonia.AssetWorkspace _workspace;
    private bool _disposed;

    public UabeaEngineBridge(AssetsManager manager, bool fromBundle)
    {
        _workspace = new UABEAvalonia.AssetWorkspace(
            manager ?? throw new ArgumentNullException(nameof(manager)), fromBundle);
    }

    public static AssetsManager CreateManager(string classDataPath)
    {
        if (string.IsNullOrWhiteSpace(classDataPath) || !File.Exists(classDataPath))
            throw new FileNotFoundException("UABEA class database was not found.", classDataPath);

        var manager = new AssetsManager();
        try
        {
            manager.LoadClassPackage(classDataPath);
            return manager;
        }
        catch
        {
            manager.UnloadAllAssetsFiles(true);
            throw;
        }
    }

    public static BundleFileInstance OpenBundle(AssetsManager manager, string path) =>
        manager.LoadBundleFile(path, false);

    public static AssetsFileInstance OpenAssets(AssetsManager manager, string path) =>
        manager.LoadAssetsFile(path, false);

    public void AddReplacer(AssetsFileInstance file, AssetsReplacer replacer) =>
        _workspace.AddReplacer(file, replacer);

    public bool TryGetPreviewData(AssetsFileInstance file, long pathId, out byte[] data)
    {
        data = Array.Empty<byte>();
        var id = new AssetID(file.path, pathId);
        if (!_workspace.NewAssetDatas.TryGetValue(id, out var stream) || stream is null)
            return false;

        if (!stream.CanRead || !stream.CanSeek)
            throw new InvalidDataException("The pending replacement stream is not seekable.");

        long originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            data = copy.ToArray();
            return true;
        }
        finally
        {
            stream.Position = Math.Min(originalPosition, stream.Length);
        }
    }

    public List<AssetsReplacer> GetReplacersForFile(AssetsFileInstance file) =>
        _workspace.NewAssets
            .Where(pair => string.Equals(pair.Key.fileName, file.path, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .ToList();

    public List<AssetID> GetAssetIdsForFile(AssetsFileInstance file) =>
        _workspace.NewAssets.Keys
            .Where(id => string.Equals(id.fileName, file.path, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public static void WriteBundleCopyWithOriginalCompression(
        BundleFileInstance sourceBundle,
        List<BundleReplacer> replacers,
        string outputPath,
        AssetBundleCompressionType originalCompression)
    {
        ArgumentNullException.ThrowIfNull(sourceBundle);
        if (replacers is null || replacers.Count == 0)
            throw new InvalidOperationException("There are no bundle changes to write.");

        string uncompressedPath = outputPath + ".uncompressed";
        try
        {
            using (var output = File.Create(uncompressedPath))
            using (var writer = new AssetsFileWriter(output))
                sourceBundle.file.Write(writer, replacers);

            var intermediateInfo = new FileInfo(uncompressedPath);
            if (!intermediateInfo.Exists || intermediateInfo.Length == 0)
                throw new InvalidDataException("The intermediate Unity bundle is empty.");

            var packManager = new AssetsManager();
            try
            {
                var uncompressedBundle = packManager.LoadBundleFile(uncompressedPath, false);
                if (uncompressedBundle.file.DataIsCompressed)
                    throw new InvalidDataException("The intermediate bundle was unexpectedly compressed.");

                using (var packedOutput = File.Create(outputPath))
                using (var packedWriter = new AssetsFileWriter(packedOutput))
                    uncompressedBundle.file.Pack(
                        uncompressedBundle.file.DataReader, packedWriter,
                        originalCompression, true, null);
            }
            finally
            {
                packManager.UnloadAllBundleFiles();
            }

            var finalInfo = new FileInfo(outputPath);
            if (!finalInfo.Exists || finalInfo.Length == 0)
                throw new InvalidDataException("The compressed Unity bundle output is empty.");
        }
        finally
        {
            try { File.Delete(uncompressedPath); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var stream in _workspace.NewAssetDatas.Values.Distinct())
            stream.Dispose();
        _workspace.NewAssetDatas.Clear();
        _workspace.NewAssets.Clear();
        _workspace.NewAssetData.Clear();
        _workspace.LoadedAssets.Clear();
        _workspace.LoadedFiles.Clear();
        _workspace.LoadedFileNames.Clear();
        _workspace.LoadedFileLookup.Clear();
    }
}
