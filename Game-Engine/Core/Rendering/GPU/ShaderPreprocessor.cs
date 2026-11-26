#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// Platform and variant options passed to <see cref="ShaderPreprocessor"/>.
/// </summary>
public sealed class ShaderCompileOptions
{
    public static readonly ShaderCompileOptions Desktop = new() { IsES = false, IsXbox = false };
    public static readonly ShaderCompileOptions OpenGLES = new() { IsES = true, IsXbox = false };

    public bool IsES { get; init; }
    public bool IsXbox { get; init; }

    /// <summary>
    /// Overrides the emitted <c>MAX_BONES</c> define. Defaults to 48 on OpenGL ES
    /// (fits within the 256-vec4 vertex uniform budget) and 128 on desktop OpenGL.
    /// </summary>
    public int? MaxBones { get; init; }

    /// <summary>Optional extra preprocessor defines (<c>#define KEY [VALUE]</c>).</summary>
    public IReadOnlyDictionary<string, string?>? Defines { get; init; }

    public int EffectiveMaxBones => MaxBones ?? (IsES ? 48 : 128);
}

/// <summary>Maps a preprocessed GLSL line back to its original file and 1-based line number.</summary>
public readonly record struct ShaderSourceLocation(string File, int Line);

/// <summary>
/// Source-line map produced by <see cref="ShaderPreprocessor"/> so GLSL compile errors
/// report the exact original <c>.glsl</c> or <c>.shader</c> file and line number.
/// </summary>
public sealed class ShaderSourceMap
{
    private readonly List<ShaderSourceLocation> _lines;

    public ShaderSourceMap(List<ShaderSourceLocation> lines)
    {
        _lines = lines;
    }

    public int LineCount => _lines.Count;

    /// <summary>Resolve a 1-based preprocessed line number to its original file and line.</summary>
    public ShaderSourceLocation? Resolve(int preprocessedLine)
    {
        int idx = preprocessedLine - 1;
        if (idx < 0 || idx >= _lines.Count) return null;
        return _lines[idx];
    }

    // Matches common GLSL driver error line references:
    // - ANGLE / Khronos / Intel: "ERROR: 0:42:" or "WARNING: 0:42:"
    // - NVIDIA:                  "0(42) :"
    // - Mesa:                    "0:42(10):"
    private static readonly Regex s_errorLineRegex = new(
        @"(?<prefix>\b(?:ERROR|WARNING)\s*:\s*\d+\s*:\s*)(?<line>\d+)(?<suffix>\s*:)|(?<prefix>\b\d+\s*\()(?<line>\d+)(?<suffix>\)\s*:)|(?<prefix>\b\d+\s*:\s*)(?<line>\d+)(?<suffix>\s*\(\d+\)\s*:)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Annotate a GLSL compiler info log with original file names and line numbers.
    /// </summary>
    public string FormatErrorLog(string glInfoLog)
    {
        if (string.IsNullOrWhiteSpace(glInfoLog) || _lines.Count == 0)
            return glInfoLog;

        return s_errorLineRegex.Replace(glInfoLog, match =>
        {
            if (int.TryParse(match.Groups["line"].Value, out int lineNum))
            {
                var loc = Resolve(lineNum);
                if (loc.HasValue)
                    return $"{match.Value} [{loc.Value.File}:{loc.Value.Line}]";
            }
            return match.Value;
        });
    }
}

/// <summary>Result of preprocessing a GLSL shader stage.</summary>
public sealed class PreprocessedShader
{
    public required string Source { get; init; }
    public required ShaderSourceMap SourceMap { get; init; }

    /// <summary>
    /// All logical shader paths and/or absolute disk file paths read while preprocessing
    /// this shader (including the root file and all transitively included headers).
    /// </summary>
    public required IReadOnlyList<string> IncludedFiles { get; init; }
}

/// <summary>
/// Loads modular GLSL shader sources (disk-first in Editor dev mode with embedded-resource fallback),
/// resolves comment/string-aware <c>#include</c> and <c>#pragma once</c> directives, emits clean
/// platform headers (<c>#version 330 core</c> vs <c>#version 300 es</c> + precision + defines),
/// and tracks exact line mappings for GLSL compiler diagnostics.
/// </summary>
public static class ShaderPreprocessor
{
    private static readonly Lazy<Dictionary<string, string>> s_embeddedResources = new(BuildEmbeddedResourceIndex);
    private static string? s_devShadersDirectory;
    private static bool s_devDirectoryProbed;
    private static readonly object s_devProbeLock = new();

    /// <summary>
    /// Optional on-disk directory containing <c>Core/Rendering/Shaders</c> (auto-discovered in Editor
    /// dev builds so editing built-in <c>.glsl</c> files hot-reloads immediately). Set to null to force
    /// loading strictly from embedded resources.
    /// </summary>
    public static string? DevShadersDirectory
    {
        get
        {
            if (!s_devDirectoryProbed)
            {
                lock (s_devProbeLock)
                {
                    if (!s_devDirectoryProbed)
                    {
                        s_devShadersDirectory = DiscoverDevShadersDirectory();
                        s_devDirectoryProbed = true;
                    }
                }
            }
            return s_devShadersDirectory;
        }
        set
        {
            lock (s_devProbeLock)
            {
                s_devShadersDirectory = string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
                s_devDirectoryProbed = true;
            }
        }
    }

    private static string? DiscoverDevShadersDirectory()
    {
#if PLAYER
        return null;
#else
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
            {
                string direct = Path.Combine(dir.FullName, "Core", "Rendering", "Shaders");
                if (File.Exists(Path.Combine(direct, "Common", "Fullscreen.vert.glsl")))
                    return Path.GetFullPath(direct);

                string nested = Path.Combine(dir.FullName, "Game-Engine", "Core", "Rendering", "Shaders");
                if (File.Exists(Path.Combine(nested, "Common", "Fullscreen.vert.glsl")))
                    return Path.GetFullPath(nested);
            }
        }
        catch { }
        return null;
#endif
    }

    private static Dictionary<string, string> BuildEmbeddedResourceIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var asm = typeof(ShaderPreprocessor).Assembly;
        foreach (string resName in asm.GetManifestResourceNames())
        {
            if (!resName.EndsWith(".glsl", StringComparison.OrdinalIgnoreCase))
                continue;

            string normalized = resName.Replace('\\', '/');
            if (normalized.StartsWith("Shaders/", StringComparison.OrdinalIgnoreCase))
            {
                string key = normalized.Substring("Shaders/".Length);
                map[key] = resName;
            }
            else
            {
                // Fallback if built without LogicalName
                const string marker = ".Core.Rendering.Shaders.";
                int idx = resName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string tail = resName.Substring(idx + marker.Length);
                    // Convert first dot (folder separator) to slash: e.g. Common.Fullscreen.vert.glsl -> Common/Fullscreen.vert.glsl
                    int firstDot = tail.IndexOf('.');
                    if (firstDot > 0)
                    {
                        string key = tail.Substring(0, firstDot) + "/" + tail.Substring(firstDot + 1);
                        map.TryAdd(key, resName);
                    }
                }
            }
        }
        return map;
    }

    /// <summary>Normalize a logical shader path to forward slashes with no leading slash.</summary>
    public static string NormalizeLogicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string p = path.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p.Substring(2);
        return p.TrimStart('/');
    }

    /// <summary>Return all known embedded built-in shader logical paths.</summary>
    public static IReadOnlyCollection<string> GetEmbeddedShaderPaths() => s_embeddedResources.Value.Keys;

    /// <summary>
    /// Try to map an absolute disk path inside <see cref="DevShadersDirectory"/> back to its
    /// logical shader path (e.g. <c>Common/Shadows.glsl</c>).
    /// </summary>
    public static bool TryGetLogicalPath(string absolutePath, out string logicalPath)
    {
        logicalPath = string.Empty;
        string? devDir = DevShadersDirectory;
        if (string.IsNullOrEmpty(devDir) || string.IsNullOrWhiteSpace(absolutePath))
            return false;

        try
        {
            string full = Path.GetFullPath(absolutePath);
            string root = devDir.EndsWith(Path.DirectorySeparatorChar)
                ? devDir
                : devDir + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                logicalPath = NormalizeLogicalPath(full.Substring(root.Length));
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Load and preprocess a built-in shader by logical path (e.g. <c>"Core/Standard.vert.glsl"</c>).
    /// </summary>
    public static PreprocessedShader PreprocessBuiltin(string logicalPath, ShaderCompileOptions? options = null)
    {
        options ??= ShaderCompileOptions.Desktop;
        string norm = NormalizeLogicalPath(logicalPath);
        if (!TryLoadRawSource(norm, baseDirectory: null, out string raw, out string? diskPath, out string resolvedId))
            throw new FileNotFoundException($"Built-in shader source not found: '{logicalPath}'");

        return PreprocessInternal(raw, resolvedId, diskPath, startLine: 1, options, adaptLegacyBones: false);
    }

    /// <summary>
    /// Preprocess an in-memory GLSL source string (e.g. from a custom <c>.shader</c> file or Surface shader).
    /// Supports <c>#include</c> relative to <paramref name="sourceFilePath"/> and built-in <c>Shaders/</c>.
    /// </summary>
    public static PreprocessedShader PreprocessSource(
        string glslSource,
        string sourceName,
        ShaderCompileOptions? options = null,
        string? sourceFilePath = null,
        int startLine = 1,
        bool adaptLegacyBones = true)
    {
        options ??= ShaderCompileOptions.Desktop;
        return PreprocessInternal(glslSource, sourceName, sourceFilePath, startLine, options, adaptLegacyBones);
    }

    /// <summary>
    /// Backward-compatible adapter for standalone GLSL strings (replaces <see cref="ShaderSources.Adapt"/>).
    /// </summary>
    public static string AdaptSource(string glsl, bool isES)
    {
        if (string.IsNullOrEmpty(glsl)) return glsl;

        // If the source contains #include directives, run full preprocessing.
        if (glsl.Contains("#include", StringComparison.Ordinal))
        {
            return PreprocessSource(glsl, "<inline>", isES ? ShaderCompileOptions.OpenGLES : ShaderCompileOptions.Desktop).Source;
        }

        glsl = glsl.TrimStart('\r', '\n', ' ', '\t');
        if (!isES) return glsl;

        glsl = glsl.Replace("#define MAX_BONES 128", "#define MAX_BONES 48");
        glsl = glsl.Replace("uniform mat4 uBones[128];", "uniform mat4 uBones[48];");
        glsl = glsl.Replace("ivec4 ids = ivec4(aBoneIds);", "ivec4 ids = clamp(ivec4(aBoneIds), 0, 47);");

        if (glsl.Contains("#version 330 core", StringComparison.Ordinal))
        {
            return glsl.Replace(
                "#version 330 core",
                "#version 300 es\nprecision highp float;\nprecision highp int;\nprecision highp sampler2D;\nprecision highp samplerCube;\n#define TARGET_ES 1");
        }

        if (!glsl.StartsWith("#version", StringComparison.Ordinal))
        {
            return "#version 300 es\nprecision highp float;\nprecision highp int;\nprecision highp sampler2D;\nprecision highp samplerCube;\n#define TARGET_ES 1\n" + glsl;
        }

        return glsl;
    }

    private static PreprocessedShader PreprocessInternal(
        string rawSource,
        string rootDisplayName,
        string? rootDiskPath,
        int startLine,
        ShaderCompileOptions options,
        bool adaptLegacyBones)
    {
        var output = new StringBuilder(rawSource.Length + 512);
        var lineMap = new List<ShaderSourceLocation>(256);
        var includedFiles = new List<string>();
        var includedFilesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pragmaOnceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void RecordDependency(string dep)
        {
            if (!string.IsNullOrWhiteSpace(dep) && includedFilesSet.Add(dep))
                includedFiles.Add(dep);
        }

        void AppendMappedLine(string text, string file, int line)
        {
            output.Append(text);
            output.Append('\n');
            lineMap.Add(new ShaderSourceLocation(file, line));
        }

        // Emit authoritative GLSL header at line 1 (ANGLE requires #version on line 1)
        int preambleLine = 1;
        if (options.IsES)
        {
            AppendMappedLine("#version 300 es", "<preamble>", preambleLine++);
            AppendMappedLine("precision highp float;", "<preamble>", preambleLine++);
            AppendMappedLine("precision highp int;", "<preamble>", preambleLine++);
            AppendMappedLine("precision highp sampler2D;", "<preamble>", preambleLine++);
            AppendMappedLine("precision highp samplerCube;", "<preamble>", preambleLine++);
            AppendMappedLine("#define TARGET_ES 1", "<preamble>", preambleLine++);
        }
        else
        {
            AppendMappedLine("#version 330 core", "<preamble>", preambleLine++);
        }

        if (options.IsXbox)
            AppendMappedLine("#define TARGET_XBOX 1", "<preamble>", preambleLine++);

        AppendMappedLine($"#define MAX_BONES {options.EffectiveMaxBones}", "<preamble>", preambleLine++);

        if (options.Defines != null && options.Defines.Count > 0)
        {
            // Sort keys deterministically so linked binary cache hashes are stable.
            foreach (var kv in options.Defines.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                string defLine = string.IsNullOrEmpty(kv.Value)
                    ? $"#define {kv.Key}"
                    : $"#define {kv.Key} {kv.Value}";
                AppendMappedLine(defLine, "<preamble>", preambleLine++);
            }
        }

        RecordDependency(rootDisplayName);
        if (!string.IsNullOrEmpty(rootDiskPath))
            RecordDependency(Path.GetFullPath(rootDiskPath));

        string? rootBaseDir = !string.IsNullOrEmpty(rootDiskPath)
            ? Path.GetDirectoryName(rootDiskPath)
            : GetLogicalDirectory(rootDisplayName);

        ExpandSource(
            rawSource,
            rootDisplayName,
            rootDiskPath,
            rootBaseDir,
            startLine,
            options,
            adaptLegacyBones,
            AppendMappedLine,
            RecordDependency,
            pragmaOnceFiles,
            activeStack);

        return new PreprocessedShader
        {
            Source = output.ToString(),
            SourceMap = new ShaderSourceMap(lineMap),
            IncludedFiles = includedFiles
        };
    }

    private static void ExpandSource(
        string source,
        string fileId,
        string? diskPath,
        string? currentDir,
        int baseLine,
        ShaderCompileOptions options,
        bool adaptLegacyBones,
        Action<string, string, int> emitLine,
        Action<string> recordDependency,
        HashSet<string> pragmaOnceFiles,
        HashSet<string> activeStack)
    {
        string stackKey = !string.IsNullOrEmpty(diskPath) ? Path.GetFullPath(diskPath) : fileId;
        if (!activeStack.Add(stackKey))
            throw new InvalidOperationException($"Circular shader #include detected involving '{fileId}'.");

        try
        {
            string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool inBlockComment = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int lineNum = baseLine + i;

                bool startedInBlockComment = inBlockComment;
                string codeOnly = StripCommentsForDirective(line, ref inBlockComment);

                if (!startedInBlockComment)
                {
                    string trimmed = codeOnly.TrimStart();
                    if (trimmed.StartsWith("#", StringComparison.Ordinal))
                    {
                        string directive = trimmed.Substring(1).TrimStart();

                        // Strip #version directives — preprocessor emits the target #version at line 1.
                        if (StartsWithKeyword(directive, "version"))
                            continue;

                        // Handle #pragma once
                        if (StartsWithKeyword(directive, "pragma"))
                        {
                            string rest = directive.Substring("pragma".Length).Trim();
                            if (string.Equals(rest, "once", StringComparison.Ordinal))
                            {
                                pragmaOnceFiles.Add(stackKey);
                                continue;
                            }
                        }

                        // Handle #include "path"
                        if (StartsWithKeyword(directive, "include"))
                        {
                            string includeArg = directive.Substring("include".Length).Trim();
                            string? includePath = ParseIncludeTarget(includeArg);
                            if (includePath == null)
                                throw new FormatException($"Malformed #include directive at {fileId}:{lineNum}: '{line.Trim()}'");

                            if (!TryResolveInclude(includePath, diskPath, currentDir, out string incSource, out string? incDiskPath, out string incDisplayId))
                                throw new FileNotFoundException($"Could not resolve #include \"{includePath}\" referenced from {fileId}:{lineNum}");

                            string incKey = !string.IsNullOrEmpty(incDiskPath) ? Path.GetFullPath(incDiskPath) : incDisplayId;
                            recordDependency(incDisplayId);
                            if (!string.IsNullOrEmpty(incDiskPath))
                                recordDependency(Path.GetFullPath(incDiskPath));

                            if (pragmaOnceFiles.Contains(incKey))
                                continue;

                            string? incDir = !string.IsNullOrEmpty(incDiskPath)
                                ? Path.GetDirectoryName(incDiskPath)
                                : GetLogicalDirectory(incDisplayId);

                            ExpandSource(
                                incSource,
                                incDisplayId,
                                incDiskPath,
                                incDir,
                                baseLine: 1,
                                options,
                                adaptLegacyBones: false,
                                emitLine,
                                recordDependency,
                                pragmaOnceFiles,
                                activeStack);
                            continue;
                        }
                    }
                }

                string emitted = line;
                if (adaptLegacyBones && options.IsES)
                {
                    int maxB = options.EffectiveMaxBones;
                    if (emitted.Contains("uniform mat4 uBones[128];", StringComparison.Ordinal))
                        emitted = emitted.Replace("uniform mat4 uBones[128];", $"uniform mat4 uBones[{maxB}];");
                    if (emitted.Contains("ivec4 ids = ivec4(aBoneIds);", StringComparison.Ordinal))
                        emitted = emitted.Replace("ivec4 ids = ivec4(aBoneIds);", $"ivec4 ids = clamp(ivec4(aBoneIds), 0, {maxB - 1});");
                }

                emitLine(emitted, fileId, lineNum);
            }
        }
        finally
        {
            activeStack.Remove(stackKey);
        }
    }

    /// <summary>
    /// Scans a single line for block/line comments and string literals, updating <paramref name="inBlockComment"/>
    /// and returning the line with comments removed (for safe directive detection).
    /// </summary>
    private static string StripCommentsForDirective(string line, ref bool inBlockComment)
    {
        var sb = new StringBuilder(line.Length);
        bool inString = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            char next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < line.Length)
                {
                    sb.Append(line[++i]);
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                continue;
            }

            if (c == '/' && next == '/')
            {
                break; // rest of line is a line comment
            }

            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool StartsWithKeyword(string text, string keyword)
    {
        if (!text.StartsWith(keyword, StringComparison.Ordinal))
            return false;
        if (text.Length == keyword.Length)
            return true;
        char next = text[keyword.Length];
        return char.IsWhiteSpace(next) || next == '"' || next == '<';
    }

    private static string? ParseIncludeTarget(string arg)
    {
        if (arg.Length < 3) return null;
        char open = arg[0];
        char close = open == '"' ? '"' : open == '<' ? '>' : '\0';
        if (close == '\0') return null;
        int end = arg.IndexOf(close, 1);
        if (end <= 1) return null;
        return arg.Substring(1, end - 1).Trim();
    }

    private static string? GetLogicalDirectory(string logicalPath)
    {
        string norm = NormalizeLogicalPath(logicalPath);
        int slash = norm.LastIndexOf('/');
        return slash > 0 ? norm.Substring(0, slash) : null;
    }

    private static bool TryResolveInclude(
        string includePath,
        string? parentDiskPath,
        string? parentDir,
        out string content,
        out string? diskPath,
        out string displayId)
    {
        // 1. If parent is a real disk file (e.g., custom .shader or dev .glsl), try relative to its directory first.
        if (!string.IsNullOrEmpty(parentDiskPath))
        {
            string? dir = Path.GetDirectoryName(parentDiskPath);
            if (!string.IsNullOrEmpty(dir))
            {
                string candidate = Path.GetFullPath(Path.Combine(dir, includePath));
                if (File.Exists(candidate))
                {
                    content = File.ReadAllText(candidate);
                    diskPath = candidate;
                    displayId = TryGetLogicalPath(candidate, out string lp) ? lp : Path.GetFileName(candidate);
                    return true;
                }
            }
        }

        // 2. If parent has a logical directory (e.g. "Core"), try relative logical path first (e.g. "Core/Foo.glsl").
        if (!string.IsNullOrEmpty(parentDir) && !Path.IsPathRooted(parentDir))
        {
            string relLogical = NormalizeLogicalPath(parentDir + "/" + includePath);
            if (TryLoadRawSource(relLogical, baseDirectory: null, out content, out diskPath, out displayId))
                return true;
        }

        // 3. Resolve relative to Shaders/ root (dev disk directory first, then embedded resources).
        string norm = NormalizeLogicalPath(includePath);
        if (TryLoadRawSource(norm, baseDirectory: null, out content, out diskPath, out displayId))
            return true;

        content = string.Empty;
        diskPath = null;
        displayId = includePath;
        return false;
    }

    private static bool TryLoadRawSource(
        string path,
        string? baseDirectory,
        out string content,
        out string? diskPath,
        out string displayId)
    {
        // Absolute path on disk
        if (Path.IsPathRooted(path) && File.Exists(path))
        {
            string full = Path.GetFullPath(path);
            content = File.ReadAllText(full);
            diskPath = full;
            displayId = TryGetLogicalPath(full, out string lp) ? lp : full;
            return true;
        }

        string norm = NormalizeLogicalPath(path);

        // Editor dev mode: load from disk Shaders/ folder for live hot-reload
        string? devDir = DevShadersDirectory;
        if (!string.IsNullOrEmpty(devDir))
        {
            string candidate = Path.GetFullPath(Path.Combine(devDir, norm.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(candidate))
            {
                content = File.ReadAllText(candidate);
                diskPath = candidate;
                displayId = norm;
                return true;
            }
        }

        // Embedded resource fallback
        if (s_embeddedResources.Value.TryGetValue(norm, out string? resName))
        {
            using var stream = typeof(ShaderPreprocessor).Assembly.GetManifestResourceStream(resName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                content = reader.ReadToEnd();
                diskPath = null;
                displayId = norm;
                return true;
            }
        }

        content = string.Empty;
        diskPath = null;
        displayId = norm;
        return false;
    }
}
