using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UABEA.Mobile;

/// <summary>
/// Android-safe bundle workspace adapted from UABEA's BundleWorkspace.
/// Tracks added, replaced, removed and renamed bundle entries and emits the
/// native BundleReplacer objects consumed by AssetsTools.NET.
/// </summary>
public sealed class BundleWorkspace : IDisposable
{
    private readonly Dictionary<string, BundleWorkspaceItem> _lookup =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BundleWorkspaceItem> _items = new();

    public BundleFileInstance? BundleInstance { get; private set; }
    public IReadOnlyList<BundleWorkspaceItem> Files => _items;
    public IReadOnlyCollection<string> RemovedFiles => _removedFiles;
    private readonly HashSet<string> _removedFiles = new(StringComparer.OrdinalIgnoreCase);
    public bool Modified => _removedFiles.Count != 0 || _items.Any(x => x.IsModified || x.Name != x.OriginalName || x.IsRemoved);

    public void Reset(BundleFileInstance? bundle)
    {
        DisposeNewStreams();
        _items.Clear();
        _lookup.Clear();
        _removedFiles.Clear();
        BundleInstance = bundle;
        if (bundle is null) return;

        foreach (var dir in bundle.file.BlockAndDirInfo.DirectoryInfos)
        {
            var segment = new SegmentStream(bundle.file.DataReader.BaseStream, dir.Offset, dir.DecompressedSize);
            var item = new BundleWorkspaceItem(dir.Name, dir.Name, false,
                (dir.Flags & 0x04) != 0, false, false, segment);
            _items.Add(item);
            _lookup[item.Name] = item;
        }
    }

    public void AddOrReplaceFile(Stream data, string name, bool isSerialized, string? previousName = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValidateName(name);
        previousName ??= name;

        if (_lookup.TryGetValue(previousName, out var old))
        {
            int index = _items.IndexOf(old);
            var replacement = new BundleWorkspaceItem(name, old.OriginalName, old.IsNew,
                isSerialized, true, false, data);
            if (index >= 0) _items[index] = replacement;
            if (old.IsNew || old.IsModified) old.Stream.Dispose();
            _lookup.Remove(previousName);
            _lookup[name] = replacement;
            _removedFiles.Remove(old.OriginalName);
            return;
        }

        var added = new BundleWorkspaceItem(name, name, true, isSerialized, true, false, data);
        _items.Add(added);
        _lookup[name] = added;
    }

    public void RenameFile(string originalName, string newName)
    {
        ValidateName(newName);
        if (!_lookup.TryGetValue(originalName, out var item))
            throw new FileNotFoundException("Bundle entry was not found.", originalName);
        if (_lookup.ContainsKey(newName) && !string.Equals(originalName, newName, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A bundle entry with that name already exists.");

        item.Name = newName;
        _lookup.Remove(originalName);
        _lookup[newName] = item;
    }

    public void RemoveFile(string name)
    {
        if (!_lookup.TryGetValue(name, out var item))
            throw new FileNotFoundException("Bundle entry was not found.", name);
        _lookup.Remove(name);
        _items.Remove(item);
        if (item.IsNew) item.Stream.Dispose();
        else _removedFiles.Add(item.OriginalName);
    }

    public List<BundleReplacer> GetReplacers()
    {
        var replacers = new List<BundleReplacer>();
        foreach (string name in _removedFiles)
            replacers.Add(new BundleRemover(name));

        foreach (var item in _items)
        {
            if (item.Stream.CanSeek) item.Stream.Position = 0;
            if (item.IsNew)
            {
                replacers.Add(new BundleReplacerFromStream(item.Name, item.Name,
                    item.IsSerialized, item.Stream, 0, -1));
            }
            else if (item.IsModified)
            {
                replacers.Add(new BundleReplacerFromStream(item.OriginalName, item.Name,
                    item.IsSerialized, item.Stream, 0, -1));
            }
            else if (!string.Equals(item.Name, item.OriginalName, StringComparison.Ordinal))
            {
                replacers.Add(new BundleRenamer(item.OriginalName, item.Name));
            }
        }
        return replacers;
    }

    public bool TryGetFile(string name, out BundleWorkspaceItem item) =>
        _lookup.TryGetValue(name, out item!);

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\' }) >= 0 ||
            name == "." || name == "..")
            throw new ArgumentException("Bundle entry name must be a non-empty file name.", nameof(name));
    }

    private void DisposeNewStreams()
    {
        foreach (var item in _items)
            if (item.IsNew || item.IsModified) item.Stream.Dispose();
    }

    public void Dispose()
    {
        DisposeNewStreams();
        _items.Clear();
        _lookup.Clear();
        _removedFiles.Clear();
        BundleInstance = null;
    }
}

public sealed class BundleWorkspaceItem
{
    public string Name { get; set; }
    public string OriginalName { get; }
    public bool IsNew { get; }
    public bool IsSerialized { get; }
    public bool IsModified { get; }
    public bool IsRemoved { get; set; }
    public Stream Stream { get; }

    internal BundleWorkspaceItem(string name, string originalName, bool isNew,
        bool isSerialized, bool isModified, bool isRemoved, Stream stream)
    {
        Name = name;
        OriginalName = originalName;
        IsNew = isNew;
        IsSerialized = isSerialized;
        IsModified = isModified;
        IsRemoved = isRemoved;
        Stream = stream;
    }
}
