using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UABEA.Mobile.Engine;

/// <summary>
/// Android-facing bridge to the same AssetsTools.NET engine used by desktop UABEA.
/// This layer owns file parsing, bundle serialization, compression restoration,
/// and post-save validation. It deliberately contains no desktop UI or VM.
/// </summary>
public static class UabeaEngineBridge
{
    public static AssetsManager CreateManager(string classDataPath)
    {
        if (string.IsNullOrWhiteSpace(classDataPath) || !File.Exists(classDataPath))
            throw new FileNotFoundException("UABEA class database package was not found.", classDataPath);

        var manager = new AssetsManager();
        try
        {
            manager.LoadClassPackage(classDataPath);
            return manager;
        }
        catch
        {
            manager.UnloadAllAssetsFiles(true);
            manager.UnloadAllBundleFiles();
            throw;
        }
    }

    public static BundleFileInstance OpenBundle(AssetsManager manager, string path)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (!File.Exists(path))
            throw new FileNotFoundException("Unity bundle was not found.", path);

        return manager.LoadBundleFile(path, false);
    }

    public static AssetsFileInstance OpenAssets(AssetsManager manager, string path)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (!AssetsFile.IsAssetsFile(path))
            throw new InvalidDataException("The selected file is not a valid Unity assets file.");

        return manager.LoadAssetsFile(path, false);
    }

    public static void WriteBundleCopyWithOriginalCompression(
        BundleFileInstance sourceBundle,
        IReadOnlyList<BundleReplacer> replacers,
        string outputPath,
        AssetBundleCompressionType originalCompression)
    {
        ArgumentNullException.ThrowIfNull(sourceBundle);
        ArgumentNullException.ThrowIfNull(replacers);
        if (replacers.Count == 0)
            throw new InvalidOperationException("There are no bundle changes to write.");

        string uncompressedPath = outputPath + ".uncompressed";
        try
        {
            using (var output = File.Create(uncompressedPath))
            using (var writer = new AssetsFileWriter(output))
                sourceBundle.file.Write(writer, replacers.ToList());

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
                        uncompressedBundle.file.DataReader,
                        packedWriter,
                        originalCompression,
                        true,
                        null);
            }
            finally
            {
                packManager.UnloadAllBundleFiles();
                packManager.UnloadAllAssetsFiles(true);
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

    public static void ValidateSavedOutput(
        string path,
        bool isBundle,
        IReadOnlyCollection<AssetID> savedAssetIds,
        string classDataPath)
    {
        var verifier = new AssetsManager();
        try
        {
            if (isBundle)
            {
                var verifiedBundle = verifier.LoadBundleFile(path, false);
                if (verifiedBundle.file.BlockAndDirInfo.DirectoryInfos.Length == 0)
                    throw new InvalidDataException("The saved Unity bundle contains no readable directory entries.");
                return;
            }

            var verifiedAssets = verifier.LoadAssetsFile(path, false);
            var availableIds = verifiedAssets.file.AssetInfos.Select(x => x.PathId).ToHashSet();
            foreach (var assetId in savedAssetIds)
                if (!availableIds.Contains(assetId.pathID))
                    throw new InvalidDataException($"Saved .assets file is missing modified asset Path ID {assetId.pathID}.");

            verifier.LoadClassPackage(classDataPath);
            verifier.LoadClassDatabaseFromPackage(verifiedAssets.file.Metadata.UnityVersion);
            foreach (var assetId in savedAssetIds)
            {
                var asset = verifiedAssets.file.AssetInfos.FirstOrDefault(x => x.PathId == assetId.pathID)
                    ?? throw new InvalidDataException($"Could not locate modified asset Path ID {assetId.pathID} after saving.");
                if (asset.GetTypeId(verifiedAssets.file) == (int)AssetClassID.Texture2D &&
                    verifier.GetBaseField(verifiedAssets, asset) is null)
                    throw new InvalidDataException($"Modified Texture2D Path ID {assetId.pathID} could not be deserialized after saving.");
            }
        }
        finally
        {
            verifier.UnloadAllAssetsFiles(true);
            verifier.UnloadAllBundleFiles();
        }
    }
}
