#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Game_Engine.Core.IO;
using SN = System.Numerics;

namespace Game_Engine.Core.Importers;

/// <summary>
/// Compact, versioned binary disk cache for Assimp-imported mesh parts (including optional shared Skeleton).
/// Stored under <c>&lt;ProjectRoot&gt;/Library/Cache/Meshes/</c> when a project is open, or
/// <c>%LOCALAPPDATA%/GameEngine/MeshCache/</c> as a fallback.
/// </summary>
public static class MeshImportCache
{
    public const int FormatVersion = 1;
    static readonly byte[] Magic = [(byte)'G', (byte)'E', (byte)'M', (byte)'C'];

    const int MaxBonesLimit = 4096;
    const int MaxMeshesLimit = 65536;
    const int MaxVerticesLimit = 10_000_000;
    const int MaxIndicesLimit = 60_000_000;

    /// <summary>Returns the active mesh cache directory (project-local when a project is open, else user-local).</summary>
    public static string GetCacheDirectory()
    {
        try
        {
            var projRoot = ProjectService.Current?.RootPath;
            if (!string.IsNullOrWhiteSpace(projRoot) && Directory.Exists(projRoot))
                return Path.Combine(projRoot, "Library", "Cache", "Meshes");
        }
        catch { }

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
                return Path.Combine(localAppData, "GameEngine", "MeshCache");
        }
        catch { }

        return Path.Combine(Path.GetTempPath(), "GameEngine", "MeshCache");
    }

    /// <summary>Computes the 16-hex-char SHA-256 cache key for a model file and import flags.</summary>
    public static string ComputeCacheKey(string absModelPath, string importFlags)
    {
        string full = Path.GetFullPath(absModelPath);
        string normalizedPath = full.Replace('\\', '/');
        if (OperatingSystem.IsWindows())
            normalizedPath = normalizedPath.ToLowerInvariant();

        var fi = new FileInfo(full);
        long length = fi.Exists ? fi.Length : 0L;
        long ticks = fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0L;

        string keyInput = $"{normalizedPath}|{length}|{ticks}|{importFlags}|{FormatVersion}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(keyInput));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Gets the full cache file path for the given model and import flags.</summary>
    public static string GetCacheFilePath(string absModelPath, string importFlags)
        => Path.Combine(GetCacheDirectory(), ComputeCacheKey(absModelPath, importFlags) + ".gemc");

    /// <summary>Returns true if a non-empty cache file already exists for this model version.</summary>
    public static bool HasValidCache(string absModelPath, string importFlags)
    {
        try
        {
            string cachePath = GetCacheFilePath(absModelPath, importFlags);
            return File.Exists(cachePath) && new FileInfo(cachePath).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to load cached mesh parts for <paramref name="absModelPath"/>.
    /// On corruption or format mismatch, deletes the bad cache file and returns false so Assimp runs seamlessly.
    /// </summary>
    public static bool TryLoad(string absModelPath, string importFlags, out List<Mesh> meshes)
    {
        meshes = new List<Mesh>();
        if (string.IsNullOrWhiteSpace(absModelPath) || !File.Exists(absModelPath))
            return false;

        string cachePath;
        try
        {
            cachePath = GetCacheFilePath(absModelPath, importFlags);
            if (!File.Exists(cachePath))
                return false;
        }
        catch
        {
            return false;
        }

        try
        {
            byte[] data = File.ReadAllBytes(cachePath);
            using var ms = new MemoryStream(data, writable: false);
            using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

            // Header: magic + version + flags
            if (data.Length < 9)
                throw new InvalidDataException("Cache file too short.");

            for (int i = 0; i < Magic.Length; i++)
            {
                if (br.ReadByte() != Magic[i])
                    throw new InvalidDataException("Invalid cache magic.");
            }

            int version = br.ReadInt32();
            if (version != FormatVersion)
                throw new InvalidDataException($"Unsupported cache version {version}.");

            byte headerFlags = br.ReadByte();
            bool hasSkeleton = (headerFlags & 1) != 0;

            Skeleton? sharedSkeleton = hasSkeleton ? ReadSkeleton(br) : null;

            int meshCount = br.ReadInt32();
            if (meshCount < 0 || meshCount > MaxMeshesLimit)
                throw new InvalidDataException($"Invalid mesh count {meshCount}.");

            var result = new List<Mesh>(meshCount);
            for (int i = 0; i < meshCount; i++)
                result.Add(ReadMesh(br, sharedSkeleton));

            if (ms.Position != ms.Length)
                throw new InvalidDataException("Unexpected trailing bytes in mesh cache.");

            meshes = result;
            Debug.WriteLine($"[MeshImportCache] HIT '{Path.GetFileName(absModelPath)}' ({result.Count} part(s))");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MeshImportCache] Corrupt/stale cache for '{Path.GetFileName(absModelPath)}': {ex.Message}; regenerating.");
            TryDeleteCorrupt(cachePath);
            meshes = new List<Mesh>();
            return false;
        }
    }

    /// <summary>
    /// Serializes <paramref name="meshes"/> to the binary cache atomically.
    /// Best-effort: failures are logged to Debug and never break model import.
    /// </summary>
    public static void Save(string absModelPath, string importFlags, IReadOnlyList<Mesh> meshes)
    {
        if (string.IsNullOrWhiteSpace(absModelPath) || meshes == null || meshes.Count == 0)
            return;

        try
        {
            // Collect optional shared skeleton (all skinned meshes from one ModelImporter run share one Skeleton).
            Skeleton? sharedSkeleton = null;
            for (int i = 0; i < meshes.Count; i++)
            {
                var sk = meshes[i]?.Skeleton;
                if (sk != null)
                {
                    if (sharedSkeleton == null)
                        sharedSkeleton = sk;
                    else if (!ReferenceEquals(sharedSkeleton, sk))
                        return; // Unexpected multi-skeleton model: skip caching rather than risk mismatch.
                }
            }

            using var ms = new MemoryStream(64 * 1024);
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(Magic);
                bw.Write(FormatVersion);
                bw.Write((byte)(sharedSkeleton != null ? 1 : 0));

                if (sharedSkeleton != null)
                    WriteSkeleton(bw, sharedSkeleton);

                bw.Write(meshes.Count);
                for (int i = 0; i < meshes.Count; i++)
                    WriteMesh(bw, meshes[i], sharedSkeleton);

                bw.Flush();
            }

            string cachePath = GetCacheFilePath(absModelPath, importFlags);
            AtomicFile.WriteAllBytesAtomic(cachePath, ms.ToArray(), keepBackup: false);
            Debug.WriteLine($"[MeshImportCache] SAVED '{Path.GetFileName(absModelPath)}' ({meshes.Count} part(s), {ms.Length / 1024.0:F1} KB)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MeshImportCache] Save failed for '{Path.GetFileName(absModelPath)}': {ex.Message}");
        }
    }

    // ── Skeleton binary I/O ───────────────────────────────────────────────

    static void WriteSkeleton(BinaryWriter bw, Skeleton skeleton)
    {
        var bones = skeleton.Bones;
        bw.Write(bones.Length);
        for (int i = 0; i < bones.Length; i++)
        {
            var b = bones[i];
            bw.Write(b.Name ?? string.Empty);
            bw.Write(b.Index);
            bw.Write(b.ParentIndex);
            WriteMatrix4x4(bw, b.OffsetMatrix);
            WriteMatrix4x4(bw, b.LocalBindTransform);
            var children = b.Children ?? Array.Empty<int>();
            WriteIntArray(bw, children);
        }
        WriteIntArray(bw, skeleton.RootBoneIndices ?? Array.Empty<int>());
    }

    static Skeleton ReadSkeleton(BinaryReader br)
    {
        int boneCount = br.ReadInt32();
        if (boneCount < 0 || boneCount > MaxBonesLimit)
            throw new InvalidDataException($"Invalid bone count {boneCount}.");

        var bones = new Bone[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            string name = br.ReadString();
            int index = br.ReadInt32();
            int parentIndex = br.ReadInt32();
            var offset = ReadMatrix4x4(br);
            var localBind = ReadMatrix4x4(br);
            int[] children = ReadIntArray(br, MaxBonesLimit);
            bones[i] = new Bone
            {
                Name = name,
                Index = index,
                ParentIndex = parentIndex,
                OffsetMatrix = offset,
                LocalBindTransform = localBind,
                Children = children
            };
        }

        int[] rootBoneIndices = ReadIntArray(br, MaxBonesLimit);
        return new Skeleton(bones, rootBoneIndices);
    }

    // ── Mesh binary I/O ───────────────────────────────────────────────────

    static void WriteMesh(BinaryWriter bw, Mesh mesh, Skeleton? sharedSkeleton)
    {
        bw.Write((int)mesh.Kind);
        bw.Write(mesh.TessA);
        bw.Write(mesh.TessB);

        byte flags = 0;
        if (mesh.Normals != null) flags |= 1 << 0;
        if (mesh.UVs != null) flags |= 1 << 1;
        if (mesh.UV2 != null) flags |= 1 << 2;
        if (mesh.BoneWeights != null) flags |= 1 << 3;
        if (mesh.BoneIndices != null) flags |= 1 << 4;
        if (mesh.Skeleton != null && ReferenceEquals(mesh.Skeleton, sharedSkeleton)) flags |= 1 << 5;
        bw.Write(flags);

        WriteVector3Array(bw, mesh.Vertices ?? Array.Empty<SN.Vector3>());
        WriteIntArray(bw, mesh.TriIndices ?? Array.Empty<int>());
        WriteIntArray(bw, mesh.LineIndices ?? Array.Empty<int>());

        if (mesh.Normals != null) WriteVector3Array(bw, mesh.Normals);
        if (mesh.UVs != null) WriteVector2Array(bw, mesh.UVs);
        if (mesh.UV2 != null) WriteVector2Array(bw, mesh.UV2);
        if (mesh.BoneWeights != null) WriteVector4Array(bw, mesh.BoneWeights);
        if (mesh.BoneIndices != null) WriteIntArray(bw, mesh.BoneIndices);
    }

    static Mesh ReadMesh(BinaryReader br, Skeleton? sharedSkeleton)
    {
        var kind = (MeshKind)br.ReadInt32();
        int tessA = br.ReadInt32();
        int tessB = br.ReadInt32();
        byte flags = br.ReadByte();

        var verts = ReadVector3Array(br, MaxVerticesLimit);
        var tris = ReadIntArray(br, MaxIndicesLimit);
        var lines = ReadIntArray(br, MaxIndicesLimit);

        var mesh = new Mesh(verts, lines, tris)
        {
            Kind = kind,
            TessA = tessA,
            TessB = tessB
        };

        if ((flags & (1 << 0)) != 0) mesh.Normals = ReadVector3Array(br, MaxVerticesLimit);
        if ((flags & (1 << 1)) != 0) mesh.UVs = ReadVector2Array(br, MaxVerticesLimit);
        if ((flags & (1 << 2)) != 0) mesh.UV2 = ReadVector2Array(br, MaxVerticesLimit);
        if ((flags & (1 << 3)) != 0) mesh.BoneWeights = ReadVector4Array(br, MaxVerticesLimit);
        if ((flags & (1 << 4)) != 0) mesh.BoneIndices = ReadIntArray(br, MaxVerticesLimit * 4);
        if ((flags & (1 << 5)) != 0) mesh.Skeleton = sharedSkeleton;

        return mesh;
    }

    // ── Bulk primitive array helpers ──────────────────────────────────────

    static void WriteMatrix4x4(BinaryWriter bw, in SN.Matrix4x4 m)
    {
        bw.Write(m.M11); bw.Write(m.M12); bw.Write(m.M13); bw.Write(m.M14);
        bw.Write(m.M21); bw.Write(m.M22); bw.Write(m.M23); bw.Write(m.M24);
        bw.Write(m.M31); bw.Write(m.M32); bw.Write(m.M33); bw.Write(m.M34);
        bw.Write(m.M41); bw.Write(m.M42); bw.Write(m.M43); bw.Write(m.M44);
    }

    static SN.Matrix4x4 ReadMatrix4x4(BinaryReader br) => new(
        br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
        br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
        br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
        br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

    static void WriteIntArray(BinaryWriter bw, int[] arr)
    {
        bw.Write(arr.Length);
        if (arr.Length == 0) return;
        if (BitConverter.IsLittleEndian)
        {
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else
        {
            for (int i = 0; i < arr.Length; i++) bw.Write(arr[i]);
        }
    }

    static int[] ReadIntArray(BinaryReader br, int maxCount)
    {
        int count = br.ReadInt32();
        if (count < 0 || count > maxCount)
            throw new InvalidDataException($"Invalid int array count {count}.");
        if (count == 0) return Array.Empty<int>();
        var arr = new int[count];
        if (BitConverter.IsLittleEndian)
        {
            br.BaseStream.ReadExactly(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else
        {
            for (int i = 0; i < count; i++) arr[i] = br.ReadInt32();
        }
        return arr;
    }

    static void WriteVector2Array(BinaryWriter bw, SN.Vector2[] arr)
    {
        bw.Write(arr.Length);
        if (arr.Length == 0) return;
        if (BitConverter.IsLittleEndian)
        {
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else
        {
            for (int i = 0; i < arr.Length; i++) { bw.Write(arr[i].X); bw.Write(arr[i].Y); }
        }
    }

    static SN.Vector2[] ReadVector2Array(BinaryReader br, int maxCount)
    {
        int count = br.ReadInt32();
        if (count < 0 || count > maxCount)
            throw new InvalidDataException($"Invalid Vector2 array count {count}.");
        if (count == 0) return Array.Empty<SN.Vector2>();
        var arr = new SN.Vector2[count];
        if (BitConverter.IsLittleEndian)
        {
            br.BaseStream.ReadExactly(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else
        {
            for (int i = 0; i < count; i++) arr[i] = new SN.Vector2(br.ReadSingle(), br.ReadSingle());
        }
        return arr;
    }

    static void WriteVector3Array(BinaryWriter bw, SN.Vector3[] arr)
    {
        bw.Write(arr.Length);
        if (arr.Length == 0) return;
        if (BitConverter.IsLittleEndian)
        {
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else
        {
            for (int i = 0; i < arr.Length; i++) { bw.Write(arr[i].X); bw.Write(arr[i].Y); bw.Write(arr[i].Z); }
        }
    }

    static SN.Vector3[] ReadVector3Array(BinaryReader br, int maxCount)
    {
        int count = br.ReadInt32();
        if (count < 0 || count > maxCount)
            throw new InvalidDataException($"Invalid Vector3 array count {count}.");
        if (count == 0) return Array.Empty<SN.Vector3>();
        var arr = new SN.Vector3[count];
        if (BitConverter.IsLittleEndian)
        {
            br.BaseStream.ReadExactly(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else
        {
            for (int i = 0; i < count; i++) arr[i] = new SN.Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        }
        return arr;
    }

    static void WriteVector4Array(BinaryWriter bw, SN.Vector4[] arr)
    {
        bw.Write(arr.Length);
        if (arr.Length == 0) return;
        if (BitConverter.IsLittleEndian)
        {
            bw.Write(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else
        {
            for (int i = 0; i < arr.Length; i++) { bw.Write(arr[i].X); bw.Write(arr[i].Y); bw.Write(arr[i].Z); bw.Write(arr[i].W); }
        }
    }

    static SN.Vector4[] ReadVector4Array(BinaryReader br, int maxCount)
    {
        int count = br.ReadInt32();
        if (count < 0 || count > maxCount)
            throw new InvalidDataException($"Invalid Vector4 array count {count}.");
        if (count == 0) return Array.Empty<SN.Vector4>();
        var arr = new SN.Vector4[count];
        if (BitConverter.IsLittleEndian)
        {
            br.BaseStream.ReadExactly(MemoryMarshal.AsBytes(arr.AsSpan()));
        }
        else
        {
            for (int i = 0; i < count; i++) arr[i] = new SN.Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        }
        return arr;
    }

    static void TryDeleteCorrupt(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
