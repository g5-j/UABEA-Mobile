using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace UABEA.Mobile;

internal static class UnityObjMeshImporter
{
    private const int MaxSourceElements = 750_000;
    private const int MaxExpandedVertices = 500_000;
    private const int MaxTriangles = 500_000;
    private const int MaxSubMeshes = 64;

    private readonly record struct VertexKey(int Position, int Uv, int Normal);

    private sealed class ObjGroup
    {
        public string Name { get; set; } = "default";
        public List<int> Indices { get; } = new();
    }

    private sealed class ParsedObj
    {
        public List<Vector3> Positions { get; } = new();
        public List<Vector2> Uvs { get; } = new();
        public List<Vector3> SourceNormals { get; } = new();
        public List<Vector3> Vertices { get; } = new();
        public List<Vector2> VertexUvs { get; } = new();
        public List<Vector3> VertexNormals { get; } = new();
        public List<bool> HasNormal { get; } = new();
        public List<ObjGroup> Groups { get; } = new();
        public Dictionary<VertexKey, int> VertexMap { get; } = new();
        public int TriangleCount { get; set; }
        public bool HasAnyUv { get; set; }
    }

    public static string Import(AssetTypeValueField mesh, string objText, bool bigEndian, string unityVersion)
    {
        if (mesh == null || mesh.IsDummy)
            throw new InvalidDataException("Mesh asset data is unavailable.");

        int major = ParseUnityMajor(unityVersion);
        if (major < 2018)
            throw new NotSupportedException("OBJ import currently supports Unity 2018 and newer Mesh vertex layouts. This avoids writing an incompatible legacy vertex-channel layout.");

        ParsedObj parsed = Parse(objText);
        GenerateMissingNormals(parsed);

        int vertexCount = parsed.Vertices.Count;
        if (vertexCount < 3 || parsed.TriangleCount < 1)
            throw new InvalidDataException("The OBJ file contains no usable triangle mesh.");

        bool hasUvs = parsed.HasAnyUv;
        int uvChannel = 4;
        int uvOffset = 24;
        int stride = hasUvs ? uvOffset + 8 : uvOffset;
        uint channelMask = (1u << 0) | (1u << 1) | (hasUvs ? (1u << uvChannel) : 0u);

        byte[] vertexBytes = BuildVertexBuffer(parsed, hasUvs, uvOffset, stride, bigEndian);
        List<int> allIndices = parsed.Groups.SelectMany(g => g.Indices).ToList();
        bool use32BitIndices = vertexCount > ushort.MaxValue || allIndices.Any(i => i > ushort.MaxValue);

        if (use32BitIndices && major < 2017)
            throw new NotSupportedException("This Unity version does not expose a supported 32-bit Mesh index format.");

        byte[] indexBytes = BuildIndexBuffer(allIndices, use32BitIndices, bigEndian);
        (Vector3 center, Vector3 extent) = CalculateBounds(parsed.Vertices);

        AssetTypeValueField vertexData = GetRequiredField(mesh, "m_VertexData");
        SetNumber(GetRequiredField(vertexData, "m_VertexCount"), (uint)vertexCount);

        var channels = GetRequiredField(vertexData, "m_Channels.Array");
        if (!channels.TemplateField.IsArray)
            throw new InvalidDataException("Mesh vertex channel array has an unexpected type.");
        channels.Children.Clear();

        const int channelCount = 14;
        for (int i = 0; i < channelCount; i++)
        {
            var item = ValueBuilder.DefaultValueFieldFromArrayTemplate(channels);
            SetNumber(GetRequiredField(item, "stream"), 0);
            SetNumber(GetRequiredField(item, "offset"), 0);
            SetNumber(GetRequiredField(item, "format"), 0);
            SetNumber(GetRequiredField(item, "dimension"), 0);
            channels.Children.Add(item);
        }

        SetChannel(channels.Children[0], 0, 0, 3);
        SetChannel(channels.Children[1], 0, 12, 3);
        if (hasUvs)
            SetChannel(channels.Children[uvChannel], 0, uvOffset, 2);

        // Unity 2018+ derives stream layouts from channel descriptors in many
        // serialized versions. If a particular TypeTree exposes m_Streams, keep
        // it consistent; otherwise the channel offsets/stride are sufficient.
        var streams = GetField(vertexData, "m_Streams.Array");
        if (streams != null && !streams.IsDummy && streams.TemplateField.IsArray)
        {
            streams.Children.Clear();
            var streamItem = ValueBuilder.DefaultValueFieldFromArrayTemplate(streams);
            SetNumberIfPresent(streamItem, "channelMask", channelMask);
            SetNumberIfPresent(streamItem, "offset", 0);
            SetNumberIfPresent(streamItem, "stride", (uint)stride);
            SetNumberIfPresent(streamItem, "dividerOp", 0);
            SetNumberIfPresent(streamItem, "frequency", 0);
            streams.Children.Add(streamItem);
        }

        bool wroteVertexPayload = false;
        wroteVertexPayload |= TrySetByteArray(vertexData, "m_Data", vertexBytes);
        wroteVertexPayload |= TrySetByteArray(vertexData, "m_DataSize", vertexBytes);
        if (!wroteVertexPayload)
            throw new NotSupportedException("This Unity Mesh layout has no supported inline vertex-data field.");

        AssetTypeValueField indexFormat = GetField(mesh, "m_IndexFormat");
        if (indexFormat == null || indexFormat.IsDummy)
        {
            if (use32BitIndices)
                throw new NotSupportedException("This Mesh layout has no index-format field and cannot safely store more than 65,535 vertices.");
        }
        else
        {
            SetNumber(indexFormat, use32BitIndices ? 1u : 0u);
        }

        var indexArray = GetRequiredField(mesh, "m_IndexBuffer.Array");
        if (indexArray.TemplateField.ValueType != AssetValueType.ByteArray)
            throw new NotSupportedException("This Mesh uses an unsupported index-buffer representation.");
        indexArray.AsByteArray = indexBytes;

        var subMeshes = GetRequiredField(mesh, "m_SubMeshes.Array");
        if (!subMeshes.TemplateField.IsArray)
            throw new InvalidDataException("Mesh submesh array has an unexpected type.");
        if (parsed.Groups.Count > MaxSubMeshes)
            throw new InvalidDataException($"The OBJ contains more than {MaxSubMeshes} submeshes.");
        subMeshes.Children.Clear();

        int indexCursor = 0;
        foreach (ObjGroup group in parsed.Groups.Where(g => g.Indices.Count >= 3))
        {
            var subMesh = ValueBuilder.DefaultValueFieldFromArrayTemplate(subMeshes);
            SetNumberIfPresent(subMesh, "firstByte", checked((uint)(indexCursor * (use32BitIndices ? 4 : 2))));
            SetNumberIfPresent(subMesh, "indexCount", checked((uint)group.Indices.Count));
            SetNumberIfPresent(subMesh, "topology", 0);
            SetNumberIfPresent(subMesh, "triangleCount", checked((uint)(group.Indices.Count / 3)));
            SetNumberIfPresent(subMesh, "baseVertex", 0);
            SetNumberIfPresent(subMesh, "firstVertex", 0);
            SetNumberIfPresent(subMesh, "vertexCount", checked((uint)vertexCount));
            SetBounds(subMesh, center, extent);
            subMeshes.Children.Add(subMesh);
            indexCursor += group.Indices.Count;
        }

        if (subMeshes.Children.Count == 0)
            throw new InvalidDataException("No valid OBJ submeshes were generated.");

        SetNumberIfPresent(mesh, "m_MeshCompression", 0);
        SetBounds(mesh, center, extent, "m_LocalAABB");
        ClearStreamData(mesh);
        return $"Imported {vertexCount:N0} vertices, {parsed.TriangleCount:N0} triangles and {subMeshes.Children.Count:N0} submeshes. OBJ material files are not imported; existing MeshRenderer material references are left unchanged.";
    }

    private static ParsedObj Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("The selected OBJ file is empty.");
        text = text.TrimStart('\\uFEFF');

        var result = new ParsedObj();
        result.Groups.Add(new ObjGroup());
        ObjGroup currentGroup = result.Groups[0];
        int lineNumber = 0;

        using var reader = new StringReader(text);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            lineNumber++;
            int comment = rawLine.IndexOf('#');
            if (comment >= 0) rawLine = rawLine[..comment];
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0) continue;

            string[] parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            try
            {
                switch (parts[0])
                {
                    case "v":
                        if (parts.Length < 4) throw new FormatException("Vertex requires X, Y and Z.");
                        float x = ParseFloat(parts[1]);
                        float y = ParseFloat(parts[2]);
                        float z = ParseFloat(parts[3]);
                        if (parts.Length == 5)
                        {
                            float w = ParseFloat(parts[4]);
                            if (Math.Abs(w) > 1e-8f) { x /= w; y /= w; z /= w; }
                        }
                        // Keep the same coordinate convention as this app's OBJ exporter.
                        result.Positions.Add(new Vector3(x, y, z));
                        if (result.Positions.Count > MaxSourceElements)
                            throw new InvalidDataException("The OBJ exceeds the vertex safety limit.");
                        break;

                    case "vt":
                        if (parts.Length < 3) throw new FormatException("Texture coordinate requires U and V.");
                        result.Uvs.Add(new Vector2(ParseFloat(parts[1]), ParseFloat(parts[2])));
                        if (result.Uvs.Count > MaxSourceElements)
                            throw new InvalidDataException("The OBJ exceeds the UV safety limit.");
                        break;

                    case "vn":
                        if (parts.Length < 4) throw new FormatException("Normal requires X, Y and Z.");
                        Vector3 normal = new(ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3]));
                        result.SourceNormals.Add(normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.Zero);
                        if (result.SourceNormals.Count > MaxSourceElements)
                            throw new InvalidDataException("The OBJ exceeds the normal safety limit.");
                        break;

                    case "g":
                    case "o":
                    case "usemtl":
                        string name = parts.Length > 1 ? string.Join("_", parts.Skip(1)) : "default";
                        if (currentGroup.Indices.Count > 0)
                        {
                            if (result.Groups.Count >= MaxSubMeshes)
                                throw new InvalidDataException($"The OBJ exceeds the {MaxSubMeshes} submesh safety limit.");
                            currentGroup = new ObjGroup { Name = name };
                            result.Groups.Add(currentGroup);
                        }
                        else
                        {
                            currentGroup.Name = name;
                        }
                        break;

                    case "f":
                        if (parts.Length < 4) throw new FormatException("Face requires at least three vertices.");
                        var face = new List<int>(parts.Length - 1);
                        for (int i = 1; i < parts.Length; i++)
                            face.Add(GetOrAddVertex(result, parts[i]));

                        for (int i = 1; i + 1 < face.Count; i++)
                        {
                            // Reverse OBJ face winding to match the app's Mesh OBJ exporter.
                            currentGroup.Indices.Add(face[0]);
                            currentGroup.Indices.Add(face[i + 1]);
                            currentGroup.Indices.Add(face[i]);
                            result.TriangleCount++;
                            if (result.TriangleCount > MaxTriangles)
                                throw new InvalidDataException("The OBJ exceeds the 500,000 triangle safety limit.");
                        }
                        break;
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentOutOfRangeException || ex is OverflowException)
            {
                throw new InvalidDataException($"Invalid OBJ data on line {lineNumber}: {ex.Message}", ex);
            }
        }

        result.Groups.RemoveAll(g => g.Indices.Count == 0);
        if (result.Positions.Count == 0)
            throw new InvalidDataException("The OBJ contains no vertex positions.");
        if (result.Vertices.Count > MaxExpandedVertices)
            throw new InvalidDataException("The OBJ expands to more than 500,000 Unity vertices.");
        return result;
    }

    private static int GetOrAddVertex(ParsedObj obj, string token)
    {
        string[] components = token.Split('/');
        if (components.Length == 0 || components[0].Length == 0)
            throw new FormatException("Face vertex has no position index.");

        int position = ResolveObjIndex(components[0], obj.Positions.Count, "position");
        int uv = components.Length > 1 && components[1].Length > 0
            ? ResolveObjIndex(components[1], obj.Uvs.Count, "UV") : -1;
        int normal = components.Length > 2 && components[2].Length > 0
            ? ResolveObjIndex(components[2], obj.SourceNormals.Count, "normal") : -1;

        var key = new VertexKey(position, uv, normal);
        if (obj.VertexMap.TryGetValue(key, out int existing))
            return existing;

        if (obj.Vertices.Count >= MaxExpandedVertices)
            throw new InvalidDataException("The OBJ expands to more than 500,000 Unity vertices.");

        int index = obj.Vertices.Count;
        obj.VertexMap.Add(key, index);
        obj.Vertices.Add(obj.Positions[position]);
        obj.VertexUvs.Add(uv >= 0 ? obj.Uvs[uv] : Vector2.Zero);
        if (uv >= 0) obj.HasAnyUv = true;
        bool hasNormal = normal >= 0 && obj.SourceNormals[normal].LengthSquared() > 1e-12f;
        obj.HasNormal.Add(hasNormal);
        obj.VertexNormals.Add(hasNormal ? Vector3.Normalize(obj.SourceNormals[normal]) : Vector3.Zero);
        return index;
    }

    private static int ResolveObjIndex(string value, int count, string kind)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index == 0)
            throw new FormatException($"Invalid {kind} index '{value}'.");
        int resolved = index > 0 ? index - 1 : count + index;
        if (resolved < 0 || resolved >= count)
            throw new FormatException($"{kind} index '{value}' is outside the available data.");
        return resolved;
    }

    private static void GenerateMissingNormals(ParsedObj obj)
    {
        var accumulated = new Vector3[obj.Vertices.Count];
        foreach (ObjGroup group in obj.Groups)
        {
            for (int i = 0; i + 2 < group.Indices.Count; i += 3)
            {
                int a = group.Indices[i];
                int b = group.Indices[i + 1];
                int c = group.Indices[i + 2];
                Vector3 normal = Vector3.Cross(obj.Vertices[b] - obj.Vertices[a], obj.Vertices[c] - obj.Vertices[a]);
                if (normal.LengthSquared() < 1e-16f) continue;
                if (!obj.HasNormal[a]) accumulated[a] += normal;
                if (!obj.HasNormal[b]) accumulated[b] += normal;
                if (!obj.HasNormal[c]) accumulated[c] += normal;
            }
        }

        for (int i = 0; i < obj.VertexNormals.Count; i++)
        {
            if (!obj.HasNormal[i])
            {
                Vector3 normal = accumulated[i];
                obj.VertexNormals[i] = normal.LengthSquared() > 1e-16f
                    ? Vector3.Normalize(normal)
                    : Vector3.UnitY;
            }
        }
    }

    private static byte[] BuildVertexBuffer(ParsedObj obj, bool hasUvs, int uvOffset, int stride, bool bigEndian)
    {
        using var memory = new MemoryStream(checked(obj.Vertices.Count * stride));
        using var writer = new AssetsFileWriter(memory);
        writer.BigEndian = bigEndian;
        for (int i = 0; i < obj.Vertices.Count; i++)
        {
            Vector3 v = obj.Vertices[i];
            Vector3 n = obj.VertexNormals[i];
            writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z);
            writer.Write(n.X); writer.Write(n.Y); writer.Write(n.Z);
            if (hasUvs)
            {
                Vector2 uv = obj.VertexUvs[i];
                writer.Write(uv.X); writer.Write(uv.Y);
            }
        }
        return memory.ToArray();
    }

    private static byte[] BuildIndexBuffer(List<int> indices, bool use32Bit, bool bigEndian)
    {
        using var memory = new MemoryStream(checked(indices.Count * (use32Bit ? 4 : 2)));
        using var writer = new AssetsFileWriter(memory);
        writer.BigEndian = bigEndian;
        foreach (int index in indices)
        {
            if (use32Bit) writer.Write(checked((uint)index));
            else writer.Write(checked((ushort)index));
        }
        return memory.ToArray();
    }

    private static (Vector3 Center, Vector3 Extent) CalculateBounds(List<Vector3> vertices)
    {
        Vector3 min = vertices[0], max = vertices[0];
        foreach (Vector3 v in vertices)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        return ((min + max) * 0.5f, (max - min) * 0.5f);
    }

    private static void SetChannel(AssetTypeValueField channel, byte stream, byte offset, byte dimension)
    {
        SetNumber(GetRequiredField(channel, "stream"), stream);
        SetNumber(GetRequiredField(channel, "offset"), offset);
        SetNumber(GetRequiredField(channel, "format"), 0);
        SetNumber(GetRequiredField(channel, "dimension"), dimension);
    }

    private static void SetBounds(AssetTypeValueField root, Vector3 center, Vector3 extent, string prefix = "localAABB")
    {
        SetVector3IfPresent(root, prefix + ".m_Center", center);
        SetVector3IfPresent(root, prefix + ".m_Extent", extent);
    }

    private static void SetVector3IfPresent(AssetTypeValueField root, string path, Vector3 value)
    {
        var field = GetField(root, path);
        if (field == null || field.IsDummy) return;
        SetNumberIfPresent(root, path + ".x", value.X);
        SetNumberIfPresent(root, path + ".y", value.Y);
        SetNumberIfPresent(root, path + ".z", value.Z);
    }

    private static void ClearStreamData(AssetTypeValueField mesh)
    {
        var stream = GetField(mesh, "m_StreamData");
        if (stream == null || stream.IsDummy) return;
        SetNumberIfPresent(stream, "offset", 0);
        SetNumberIfPresent(stream, "size", 0);
        var path = GetField(stream, "path");
        if (path != null && !path.IsDummy && path.TemplateField.ValueType == AssetValueType.String)
            path.AsString = string.Empty;
    }

    private static bool TrySetByteArray(AssetTypeValueField root, string path, byte[] data)
    {
        var field = GetField(root, path);
        if (field == null || field.IsDummy || field.TemplateField.ValueType != AssetValueType.ByteArray)
            return false;
        field.AsByteArray = data;
        return true;
    }

    private static void SetNumberIfPresent(AssetTypeValueField root, string path, uint value)
    {
        var field = GetField(root, path);
        if (field != null && !field.IsDummy) SetNumber(field, value);
    }

    private static void SetNumberIfPresent(AssetTypeValueField root, string path, float value)
    {
        var field = GetField(root, path);
        if (field != null && !field.IsDummy) SetNumber(field, value);
    }

    private static void SetNumber(AssetTypeValueField field, uint value)
    {
        if (field == null || field.IsDummy || field.Value == null)
            throw new InvalidDataException("A required Mesh numeric field is unavailable.");
        switch (field.TemplateField.ValueType)
        {
            case AssetValueType.Int8: field.AsSByte = checked((sbyte)value); break;
            case AssetValueType.UInt8: field.AsByte = checked((byte)value); break;
            case AssetValueType.Int16: field.AsShort = checked((short)value); break;
            case AssetValueType.UInt16: field.AsUShort = checked((ushort)value); break;
            case AssetValueType.Int32: field.AsInt = checked((int)value); break;
            case AssetValueType.UInt32: field.AsUInt = value; break;
            case AssetValueType.Int64: field.AsLong = value; break;
            case AssetValueType.UInt64: field.AsULong = value; break;
            default: throw new InvalidDataException($"Mesh field '{field.TemplateField.Name}' is not an integer.");
        }
    }

    private static void SetNumber(AssetTypeValueField field, float value)
    {
        if (field == null || field.IsDummy || field.Value == null)
            throw new InvalidDataException("A required Mesh numeric field is unavailable.");
        switch (field.TemplateField.ValueType)
        {
            case AssetValueType.Float: field.AsFloat = value; break;
            case AssetValueType.Double: field.AsDouble = value; break;
            default: throw new InvalidDataException($"Mesh field '{field.TemplateField.Name}' is not floating-point.");
        }
    }

    private static AssetTypeValueField GetRequiredField(AssetTypeValueField root, string path)
    {
        var field = GetField(root, path);
        if (field == null || field.IsDummy)
            throw new NotSupportedException($"This Unity Mesh layout is missing required field '{path}'.");
        return field;
    }

    private static AssetTypeValueField? GetField(AssetTypeValueField root, string path)
    {
        if (root == null || root.IsDummy) return null;
        AssetTypeValueField current = root;
        foreach (string part in path.Split('.'))
        {
            if (current.IsDummy) return null;
            current = current[part];
            if (current.IsDummy) return current;
        }
        return current;
    }

    private static int ParseUnityMajor(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new NotSupportedException("The Unity version is missing; OBJ import cannot safely choose a Mesh vertex layout.");
        string digits = new string(version.TakeWhile(char.IsDigit).ToArray());
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int major))
            throw new NotSupportedException($"Unable to parse Unity version '{version}'.");
        return major;
    }

    private static float ParseFloat(string value)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
            float.IsNaN(parsed) || float.IsInfinity(parsed))
            throw new FormatException($"Invalid finite number '{value}'.");
        return parsed;
    }
}
