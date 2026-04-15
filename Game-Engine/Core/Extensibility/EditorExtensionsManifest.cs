using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Game_Engine.Core.Extensibility;

/// <summary>Optional manifest beside hot-loaded editor script DLLs (<c>Builds/EditorScripts/editor-extensions.json</c>).</summary>
public sealed class EditorExtensionsManifest
{
    public const string FileName = "editor-extensions.json";

    /// <summary>Must be 1 for this format.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Optional minimum engine version (matches <see cref="ProjectService.EngineVersion"/> semantics, e.g. <c>0.0.1</c>).</summary>
    public string? MinEngineVersion { get; set; }

    /// <summary>Optional maximum engine version (inclusive). Reject loading if the editor version is greater.</summary>
    public string? MaxEngineVersion { get; set; }

    /// <summary>
    /// Filenames to load from the EditorScripts folder, in order (e.g. <c>EditorScripts_20260101.dll</c>, <c>MyPack.dll</c>).
    /// When null or empty, every <c>EditorScripts_*.dll</c> in the folder is loaded, sorted by filename (ordinal, case-insensitive).
    /// </summary>
    public List<string>? Assemblies { get; set; }

    /// <summary>Optional label for logs and UI.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Optional version string for logs and UI.</summary>
    public string? Version { get; set; }

    /// <summary>Optional author string for logs and UI.</summary>
    public string? Author { get; set; }

    /// <summary>Optional short description for validation / future UI.</summary>
    public string? Description { get; set; }

    /// <summary>Optional URL (homepage, docs, source).</summary>
    public string? HomepageUrl { get; set; }

    /// <summary>
    /// Filenames (under EditorScripts) that must exist before any DLL from this manifest loads.
    /// Use for multi-file add-ons (content beside assemblies).
    /// </summary>
    public List<string>? DependsOnFiles { get; set; }

    /// <summary>
    /// Optional SHA-256 hex fingerprints for assemblies you ship. When set, each listed file that is part of the
    /// resolved load set must match its hash or the whole EditorScripts load is skipped.
    /// </summary>
    public List<TrustedAssemblyFingerprint>? TrustedAssemblies { get; set; }

    public static EditorExtensionsManifest? TryLoad(string jsonPath) =>
        TryLoad(jsonPath, out _);

    public static EditorExtensionsManifest? TryLoad(string jsonPath, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(jsonPath))
            {
                error = null;
                return null;
            }

            var json = File.ReadAllText(jsonPath);
            var m = JsonSerializer.Deserialize<EditorExtensionsManifest>(json, JsonOptions);
            if (m == null)
            {
                error = "Manifest JSON deserialized to null.";
                return null;
            }

            if (m.SchemaVersion != 1)
            {
                error = $"Unsupported schemaVersion {m.SchemaVersion} (expected 1).";
                return null;
            }

            return m;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Returns full paths to DLLs to load, or null if constraints reject loading.</summary>
    public static IReadOnlyList<string>? ResolveDllPaths(string editorScriptsDir, EditorExtensionsManifest? manifest, string currentEngineVersion, out string? skipReason)
    {
        skipReason = null;
        if (manifest?.MinEngineVersion is { Length: > 0 } min)
        {
            if (!TryParseVersionTriplet(min, out var minV))
            {
                skipReason = $"Invalid MinEngineVersion in manifest: {min}";
                return null;
            }

            if (!TryParseVersionTriplet(currentEngineVersion, out var curV))
                curV = new Version(0, 0, 0);
            if (curV < minV)
            {
                skipReason = $"Engine {currentEngineVersion} is below manifest MinEngineVersion {min}";
                return null;
            }
        }

        if (manifest?.MaxEngineVersion is { Length: > 0 } maxStr)
        {
            if (!TryParseVersionTriplet(maxStr, out var maxV))
            {
                skipReason = $"Invalid MaxEngineVersion in manifest: {maxStr}";
                return null;
            }

            if (!TryParseVersionTriplet(currentEngineVersion, out var curV2))
                curV2 = new Version(0, 0, 0);
            if (curV2 > maxV)
            {
                skipReason = $"Engine {currentEngineVersion} is above manifest MaxEngineVersion {maxStr}";
                return null;
            }
        }

        if (!Directory.Exists(editorScriptsDir))
        {
            skipReason = "EditorScripts folder missing";
            return null;
        }

        if (manifest?.DependsOnFiles is { Count: > 0 } deps)
        {
            foreach (var raw in deps)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var safe = Path.GetFileName(raw.Trim());
                var full = Path.Combine(editorScriptsDir, safe);
                if (!File.Exists(full))
                {
                    skipReason = $"DependsOnFiles: required file missing: {safe}";
                    return null;
                }
            }
        }

        var allScripts = Directory.EnumerateFiles(editorScriptsDir, "EditorScripts_*.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> result;
        if (manifest?.Assemblies is { Count: > 0 } list)
        {
            result = new List<string>(list.Count);
            foreach (var name in list)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var safe = Path.GetFileName(name.Trim());
                var full = Path.Combine(editorScriptsDir, safe);
                if (File.Exists(full))
                    result.Add(full);
                else
                    Log.Warning($"[Ext] Manifest lists missing assembly (skipped): {safe}");
            }
        }
        else
            result = allScripts;

        if (manifest?.TrustedAssemblies is { Count: > 0 } trusted)
        {
            foreach (var t in trusted)
            {
                if (t == null || string.IsNullOrWhiteSpace(t.File)) continue;
                var safe = Path.GetFileName(t.File.Trim());
                var full = Path.Combine(editorScriptsDir, safe);
                var expected = NormalizeHex(t.Sha256);
                if (expected.Length == 0)
                {
                    skipReason = $"TrustedAssemblies: missing or invalid sha256 for {safe}";
                    return null;
                }

                if (!result.Any(p => string.Equals(Path.GetFileName(p), safe, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (!File.Exists(full))
                {
                    skipReason = $"TrustedAssemblies: file not found for fingerprint: {safe}";
                    return null;
                }

                if (!TryComputeSha256Hex(full, out var actual) || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    skipReason = $"TrustedAssemblies: SHA-256 mismatch for {safe} (manifest trust check failed)";
                    return null;
                }
            }
        }

        return result;
    }

    private static string NormalizeHex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.Trim())
        {
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool TryComputeSha256Hex(string filePath, out string hex)
    {
        hex = "";
        try
        {
            using var fs = File.OpenRead(filePath);
            var hash = SHA256.HashData(fs);
            hex = Convert.ToHexString(hash);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryParseVersionTriplet(string s, out Version v)
    {
        v = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        try
        {
            var major = int.Parse(parts[0]);
            var minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
            var build = parts.Length > 2 ? int.Parse(parts[2]) : 0;
            v = new Version(major, minor, build);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

/// <summary>Optional SHA-256 fingerprint for a DLL under EditorScripts (see <see cref="EditorExtensionsManifest.TrustedAssemblies"/>).</summary>
public sealed class TrustedAssemblyFingerprint
{
    public string File { get; set; } = "";
    public string Sha256 { get; set; } = "";
}
