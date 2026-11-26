#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Silk.NET.OpenGL;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Immutable, value-equatable set of preprocessor defines identifying a shader variant.
/// </summary>
public sealed class ShaderVariantKey : IEquatable<ShaderVariantKey>
{
    public static readonly ShaderVariantKey Empty = new(Array.Empty<KeyValuePair<string, string?>>());

    private readonly KeyValuePair<string, string?>[] _defines;
    private readonly string _canonical;
    private readonly int _hash;

    private ShaderVariantKey(KeyValuePair<string, string?>[] sortedDefines)
    {
        _defines = sortedDefines;
        if (sortedDefines.Length == 0)
        {
            _canonical = string.Empty;
            _hash = 0;
            return;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < sortedDefines.Length; i++)
        {
            if (i > 0) sb.Append(';');
            sb.Append(sortedDefines[i].Key);
            if (!string.IsNullOrEmpty(sortedDefines[i].Value))
                sb.Append('=').Append(sortedDefines[i].Value);
        }
        _canonical = sb.ToString();
        _hash = StringComparer.Ordinal.GetHashCode(_canonical);
    }

    public bool IsEmpty => _defines.Length == 0;

    public static ShaderVariantKey Of(params (string Name, string? Value)[] defines)
    {
        if (defines == null || defines.Length == 0) return Empty;
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, val) in defines)
        {
            if (!string.IsNullOrWhiteSpace(name))
                dict[name.Trim()] = val;
        }
        if (dict.Count == 0) return Empty;
        var arr = dict.OrderBy(k => k.Key, StringComparer.Ordinal).ToArray();
        return new ShaderVariantKey(arr);
    }

    public static ShaderVariantKey FromDefines(IReadOnlyDictionary<string, string?>? defines)
    {
        if (defines == null || defines.Count == 0) return Empty;
        var arr = defines
            .Where(k => !string.IsNullOrWhiteSpace(k.Key))
            .OrderBy(k => k.Key, StringComparer.Ordinal)
            .ToArray();
        return arr.Length == 0 ? Empty : new ShaderVariantKey(arr);
    }

    public IReadOnlyDictionary<string, string?>? ToDictionary()
    {
        if (_defines.Length == 0) return null;
        var d = new Dictionary<string, string?>(_defines.Length, StringComparer.Ordinal);
        for (int i = 0; i < _defines.Length; i++)
            d[_defines[i].Key] = _defines[i].Value;
        return d;
    }

    public bool Equals(ShaderVariantKey? other)
        => other is not null && string.Equals(_canonical, other._canonical, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ShaderVariantKey other && Equals(other);
    public override int GetHashCode() => _hash;
    public override string ToString() => _canonical;
}

/// <summary>
/// Single authoritative shader owner and variant cache for one OpenGL context (<see cref="GL"/>).
/// Supports built-in modular <c>.glsl</c> shaders and custom <c>.shader</c> files with:
/// <list type="bullet">
///   <item>Variant caching keyed by shader paths/pass + preprocessor defines.</item>
///   <item>Dependency tracking across transitive <c>#include</c> files.</item>
///   <item>Safe hot-reload: compiles a candidate program first; on failure keeps the last valid
///   <see cref="ShaderProgram"/> active, logs the line-mapped error, and retries automatically on the next save.</item>
/// </list>
/// </summary>
public sealed class ShaderLibrary : IDisposable
{
    private readonly record struct BuiltinKey(string VertPath, string FragPath, ShaderVariantKey Variant);
    private readonly record struct CustomKey(string NormalizedAbsPath, ShaderPassType Pass, ShaderVariantKey Variant);

    private sealed class ShaderEntry
    {
        public ShaderProgram? Program;
        public ParsedShaderFile? ParsedMeta;
        public HashSet<string> Dependencies = new(StringComparer.OrdinalIgnoreCase);
        public long DirtyVersion = 1;      // incremented when any dependency changes
        public long CompiledVersion = 0;   // version at last compile attempt
        public bool IsDirty => Volatile.Read(ref DirtyVersion) != CompiledVersion;
    }

    private static readonly ConditionalWeakTable<GL, ShaderLibrary> s_byContext = new();
    private static readonly object s_registryLock = new();
    private static readonly List<WeakReference<ShaderLibrary>> s_allLibraries = new();

    private readonly GL _gl;
    private readonly Dictionary<BuiltinKey, ShaderEntry> _builtins = new();
    private readonly Dictionary<CustomKey, ShaderEntry> _customs = new();
    private readonly object _entryLock = new();
    private long _reloadGeneration;
    private bool _disposed;

    public bool IsES { get; }
    public bool IsXbox { get; }

    /// <summary>Monotonic counter incremented whenever any shader in this library is marked dirty.</summary>
    public long ReloadGeneration => Volatile.Read(ref _reloadGeneration);

    private ShaderLibrary(GL gl, bool isES, bool isXbox)
    {
        _gl = gl;
        IsES = isES;
        IsXbox = isXbox;

        lock (s_registryLock)
        {
            PruneDeadLibrariesLocked();
            s_allLibraries.Add(new WeakReference<ShaderLibrary>(this));
        }
    }

    /// <summary>
    /// Get or create the authoritative <see cref="ShaderLibrary"/> for a given Silk.NET <see cref="GL"/> context.
    /// </summary>
    public static ShaderLibrary GetOrCreate(GL gl, bool isES, bool? isXbox = null)
    {
#if XBOX_UWP
        bool xbox = isXbox ?? true;
#else
        bool xbox = isXbox ?? false;
#endif
        lock (s_registryLock)
        {
            if (s_byContext.TryGetValue(gl, out var existing) && !existing._disposed)
                return existing;

            var created = new ShaderLibrary(gl, isES, xbox);
            s_byContext.AddOrUpdate(gl, created);
            return created;
        }
    }

    /// <summary>
    /// Retrieve the existing <see cref="ShaderLibrary"/> for <paramref name="gl"/>, or create one by detecting ES
    /// from the GL version string as a fallback (only if no context owner registered it yet).
    /// </summary>
    public static ShaderLibrary GetOrCreate(GL gl)
    {
        lock (s_registryLock)
        {
            if (s_byContext.TryGetValue(gl, out var existing) && !existing._disposed)
                return existing;
        }

        bool isES = true;
        try
        {
            isES = gl.GetStringS(StringName.Version)?.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch { isES = true; }

        return GetOrCreate(gl, isES);
    }

    /// <summary>Try to fetch an already-registered library without creating one.</summary>
    public static bool TryGet(GL gl, out ShaderLibrary? library)
    {
        lock (s_registryLock)
        {
            if (s_byContext.TryGetValue(gl, out var existing) && !existing._disposed)
            {
                library = existing;
                return true;
            }
        }
        library = null;
        return false;
    }

    /// <summary>Dispose and detach the <see cref="ShaderLibrary"/> associated with <paramref name="gl"/>.</summary>
    public static void RemoveContext(GL gl)
    {
        ShaderLibrary? lib = null;
        lock (s_registryLock)
        {
            if (s_byContext.TryGetValue(gl, out var existing))
            {
                s_byContext.Remove(gl);
                lib = existing;
            }
        }
        lib?.Dispose();
    }

    // ── Built-In Shader Retrieval ─────────────────────────────────────────────

    /// <summary>
    /// Get or compile a built-in shader from logical paths under <c>Core/Rendering/Shaders/</c>.
    /// Throws on initial compile failure unless <paramref name="throwOnInitialError"/> is false;
    /// on hot-reload failure logs the error and preserves the last valid <see cref="ShaderProgram"/>.
    /// </summary>
    public ShaderProgram? GetBuiltin(
        string vertPath,
        string fragPath,
        ShaderVariantKey? variant = null,
        bool throwOnInitialError = true)
    {
        if (_disposed) return null;

        string normVert = ShaderPreprocessor.NormalizeLogicalPath(vertPath);
        string normFrag = ShaderPreprocessor.NormalizeLogicalPath(fragPath);
        var key = new BuiltinKey(normVert, normFrag, variant ?? ShaderVariantKey.Empty);

        ShaderEntry entry;
        lock (_entryLock)
        {
            if (!_builtins.TryGetValue(key, out entry!))
            {
                entry = new ShaderEntry();
                entry.Dependencies.Add(normVert);
                entry.Dependencies.Add(normFrag);
                _builtins[key] = entry;
            }
        }

        if (!entry.IsDirty)
            return entry.Program;

        long targetVersion = Volatile.Read(ref entry.DirtyVersion);
        bool isReload = entry.Program != null;
        string label = $"{normVert} + {normFrag}" + (key.Variant.IsEmpty ? "" : $" [{key.Variant}]");

        try
        {
            var opts = new ShaderCompileOptions
            {
                IsES = IsES,
                IsXbox = IsXbox,
                Defines = key.Variant.ToDictionary()
            };

            var prepVert = ShaderPreprocessor.PreprocessBuiltin(normVert, opts);
            var prepFrag = ShaderPreprocessor.PreprocessBuiltin(normFrag, opts);

            var candidate = new ShaderProgram(
                _gl,
                prepVert.Source,
                prepFrag.Source,
                prepVert.SourceMap,
                prepFrag.SourceMap,
                label);

            var old = entry.Program;
            var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normVert, normFrag };
            foreach (var d in prepVert.IncludedFiles) deps.Add(NormalizeDependencyKey(d));
            foreach (var d in prepFrag.IncludedFiles) deps.Add(NormalizeDependencyKey(d));

            lock (_entryLock)
            {
                entry.Program = candidate;
                entry.Dependencies = deps;
                entry.CompiledVersion = targetVersion;
            }

            old?.Dispose();

            if (isReload)
                Log.Success($"[ShaderLibrary] Hot-reloaded built-in shader: {label}");

            return candidate;
        }
        catch (Exception ex)
        {
            lock (_entryLock)
            {
                entry.CompiledVersion = targetVersion;
            }

            Log.Error($"[ShaderLibrary] Failed to compile {label}: {ex.Message}");
            if (!isReload && throwOnInitialError)
                throw;

            // Keep last valid program active on hot-reload error!
            return entry.Program;
        }
    }

    // ── Custom .shader File Retrieval ─────────────────────────────────────────

    /// <summary>
    /// Get or compile a custom <c>.shader</c> file for the requested pass and variant.
    /// Returns null only if no valid compilation has ever succeeded; on hot-reload error
    /// preserves the previous valid <see cref="ShaderProgram"/> and retries on next file save.
    /// </summary>
    public ShaderProgram? GetCustom(
        string absolutePath,
        ShaderPassType pass = ShaderPassType.Forward,
        ShaderVariantKey? variant = null)
    {
        if (_disposed || string.IsNullOrWhiteSpace(absolutePath))
            return null;

        string fullPath;
        try { fullPath = Path.GetFullPath(absolutePath); }
        catch { fullPath = absolutePath; }

        var key = new CustomKey(fullPath, pass, variant ?? ShaderVariantKey.Empty);

        ShaderEntry entry;
        lock (_entryLock)
        {
            if (!_customs.TryGetValue(key, out entry!))
            {
                entry = new ShaderEntry();
                entry.Dependencies.Add(NormalizeDependencyKey(fullPath));
                _customs[key] = entry;
            }
        }

        if (!entry.IsDirty)
            return entry.Program;

        long targetVersion = Volatile.Read(ref entry.DirtyVersion);
        bool isReload = entry.Program != null;
        string fileName = Path.GetFileName(fullPath);
        string label = $"{fileName} ({pass})" + (key.Variant.IsEmpty ? "" : $" [{key.Variant}]");

        try
        {
            if (!File.Exists(fullPath))
            {
                Log.Warning($"[ShaderLibrary] Shader file not found: {fullPath}");
                lock (_entryLock) { entry.CompiledVersion = targetVersion; }
                return entry.Program;
            }

            string content = File.ReadAllText(fullPath);
            var parsed = ShaderFileParser.Parse(content, Path.GetFileNameWithoutExtension(fullPath));
            var (vertSrc, vertStartLine, fragSrc, fragStartLine) = ShaderFileParser.GeneratePassSources(parsed, pass);

            if (string.IsNullOrWhiteSpace(vertSrc) || string.IsNullOrWhiteSpace(fragSrc))
            {
                Log.Warning($"[ShaderLibrary] Could not parse VERTEX/FRAGMENT or Surface blocks from: {fileName}");
                lock (_entryLock)
                {
                    entry.ParsedMeta = parsed;
                    entry.CompiledVersion = targetVersion;
                }
                return entry.Program;
            }

            var opts = new ShaderCompileOptions
            {
                IsES = IsES,
                IsXbox = IsXbox,
                Defines = key.Variant.ToDictionary()
            };

            var prepVert = ShaderPreprocessor.PreprocessSource(vertSrc, fileName, opts, fullPath, vertStartLine);
            var prepFrag = ShaderPreprocessor.PreprocessSource(fragSrc, fileName, opts, fullPath, fragStartLine);

            var candidate = new ShaderProgram(
                _gl,
                prepVert.Source,
                prepFrag.Source,
                prepVert.SourceMap,
                prepFrag.SourceMap,
                label);

            var old = entry.Program;
            var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NormalizeDependencyKey(fullPath) };
            foreach (var d in prepVert.IncludedFiles) deps.Add(NormalizeDependencyKey(d));
            foreach (var d in prepFrag.IncludedFiles) deps.Add(NormalizeDependencyKey(d));

            lock (_entryLock)
            {
                entry.Program = candidate;
                entry.ParsedMeta = parsed;
                entry.Dependencies = deps;
                entry.CompiledVersion = targetVersion;
            }

            old?.Dispose();

            if (isReload)
                Log.Success($"[ShaderLibrary] Hot-reloaded custom shader: {fileName}");
            else
                Log.Success($"[ShaderLibrary] Compiled custom shader: {fileName}");

            return candidate;
        }
        catch (Exception ex)
        {
            lock (_entryLock)
            {
                entry.CompiledVersion = targetVersion;
            }
            Log.Error($"[ShaderLibrary] Failed to compile {label}: {ex.Message}");
            // Keep last valid ShaderProgram active!
            return entry.Program;
        }
    }

    /// <summary>Alias for <see cref="GetCustom"/> for readability.</summary>
    public ShaderProgram? GetCustomShader(
        string absolutePath,
        ShaderPassType pass = ShaderPassType.Forward,
        ShaderVariantKey? variant = null)
        => GetCustom(absolutePath, pass, variant);

    /// <summary>Return parsed metadata (Properties + RenderState) for a loaded custom shader, if available.</summary>
    public ParsedShaderFile? GetCustomMetadata(string absolutePath, ShaderPassType pass = ShaderPassType.Forward)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;
        string fullPath;
        try { fullPath = Path.GetFullPath(absolutePath); }
        catch { fullPath = absolutePath; }

        var key = new CustomKey(fullPath, pass, ShaderVariantKey.Empty);
        lock (_entryLock)
        {
            if (_customs.TryGetValue(key, out var entry) && entry.ParsedMeta != null)
                return entry.ParsedMeta;
        }
        return ShaderFileParser.GetCachedSchema(fullPath);
    }

    /// <summary>Try to retrieve parsed metadata for a custom <c>.shader</c> file (from cache or CPU schema parser).</summary>
    public bool TryGetCustomShaderInfo(string absolutePath, out ParsedShaderFile? doc)
    {
        doc = GetCustomMetadata(absolutePath);
        return doc != null;
    }

    /// <summary>Returns true if the <c>.shader</c> file uses a <c>Surface { ... }</c> block (CPU-only, no GL context needed).</summary>
    public static bool IsSurfaceShader(string? absolutePath)
        => ShaderFileParser.GetCachedSchema(absolutePath)?.IsSurfaceShader == true;

    // ── Cross-Context File Change Notification & Invalidation ─────────────────

    /// <summary>
    /// Notify all live <see cref="ShaderLibrary"/> instances that a <c>.glsl</c> or <c>.shader</c>
    /// file changed on disk. Marks every dependent built-in and custom shader dirty so it safely
    /// hot-reloads on its next draw.
    /// </summary>
    public static int NotifyFileChanged(string changedPath)
    {
        if (string.IsNullOrWhiteSpace(changedPath)) return 0;

        ShaderFileParser.InvalidateSchemaCache(changedPath);

        string absKey = NormalizeDependencyKey(changedPath);
        string? logicalKey = ShaderPreprocessor.TryGetLogicalPath(changedPath, out string lp)
            ? NormalizeDependencyKey(lp)
            : null;

        int marked = 0;
        List<ShaderLibrary> live = SnapshotLiveLibraries();
        foreach (var lib in live)
        {
            marked += lib.MarkDirtyForPath(absKey, logicalKey);
        }
        return marked;
    }

    /// <summary>Dispose and clear all cached custom shaders across every active GL context.</summary>
    public static void ClearAllCustomShaders()
    {
        ShaderFileParser.InvalidateSchemaCache(null);
        List<ShaderLibrary> live = SnapshotLiveLibraries();
        foreach (var lib in live)
            lib.ClearCustomShaders();
    }

    private int MarkDirtyForPath(string absKey, string? logicalKey)
    {
        if (_disposed) return 0;
        int marked = 0;

        lock (_entryLock)
        {
            foreach (var entry in _builtins.Values)
            {
                if (DependsOn(entry.Dependencies, absKey, logicalKey))
                {
                    Interlocked.Increment(ref entry.DirtyVersion);
                    marked++;
                }
            }

            foreach (var entry in _customs.Values)
            {
                if (DependsOn(entry.Dependencies, absKey, logicalKey))
                {
                    Interlocked.Increment(ref entry.DirtyVersion);
                    marked++;
                }
            }
        }

        if (marked > 0)
            Interlocked.Increment(ref _reloadGeneration);

        return marked;
    }

    private static bool DependsOn(HashSet<string> deps, string absKey, string? logicalKey)
    {
        if (deps.Contains(absKey)) return true;
        if (!string.IsNullOrEmpty(logicalKey) && deps.Contains(logicalKey)) return true;
        return false;
    }

    private static string NormalizeDependencyKey(string path)
    {
        string trimmed = path.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            try { return Path.GetFullPath(trimmed).Replace('\\', '/'); }
            catch { return trimmed.Replace('\\', '/'); }
        }
        return ShaderPreprocessor.NormalizeLogicalPath(trimmed);
    }

    public void ClearCustomShaders()
    {
        if (_disposed) return;
        List<ShaderProgram> toDispose = new();
        lock (_entryLock)
        {
            foreach (var entry in _customs.Values)
            {
                if (entry.Program != null)
                    toDispose.Add(entry.Program);
            }
            _customs.Clear();
        }
        foreach (var p in toDispose)
            p.Dispose();
    }

    private static List<ShaderLibrary> SnapshotLiveLibraries()
    {
        var result = new List<ShaderLibrary>();
        lock (s_registryLock)
        {
            for (int i = s_allLibraries.Count - 1; i >= 0; i--)
            {
                if (s_allLibraries[i].TryGetTarget(out var lib) && !lib._disposed)
                    result.Add(lib);
                else
                    s_allLibraries.RemoveAt(i);
            }
        }
        return result;
    }

    private static void PruneDeadLibrariesLocked()
    {
        for (int i = s_allLibraries.Count - 1; i >= 0; i--)
        {
            if (!s_allLibraries[i].TryGetTarget(out var lib) || lib._disposed)
                s_allLibraries.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        List<ShaderProgram> toDispose = new();
        lock (_entryLock)
        {
            foreach (var entry in _builtins.Values)
                if (entry.Program != null) toDispose.Add(entry.Program);
            foreach (var entry in _customs.Values)
                if (entry.Program != null) toDispose.Add(entry.Program);
            _builtins.Clear();
            _customs.Clear();
        }

        foreach (var p in toDispose)
            p.Dispose();

        lock (s_registryLock)
        {
            s_byContext.Remove(_gl);
            PruneDeadLibrariesLocked();
        }
    }
}
