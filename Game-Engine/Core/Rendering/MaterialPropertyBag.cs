#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia.Media;
using SN = System.Numerics;

namespace Game_Engine.Core.Rendering;

/// <summary>
/// Dynamic typed property overrides for a <see cref="Material"/>, keyed by GLSL uniform name
/// (e.g. <c>_Tint</c>, <c>_Roughness</c>, <c>_DetailTex</c>).
/// Stored alongside standard PBR fields so custom <c>.shader</c> files (both <c>Surface</c>
/// and explicit <c>VERTEX</c>/<c>FRAGMENT</c>) can be authored, persisted, cloned, and bound
/// cleanly across Editor and Player builds.
/// </summary>
public sealed class MaterialPropertyBag
{
    public Dictionary<string, float> Floats { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Ints { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SN.Vector4> Vectors { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Color> Colors { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> TexturePaths { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Texture2D?> Textures { get; } = new(StringComparer.Ordinal);

    public bool IsEmpty =>
        Floats.Count == 0
        && Ints.Count == 0
        && Vectors.Count == 0
        && Colors.Count == 0
        && TexturePaths.Count == 0
        && Textures.Count == 0;

    public void SetFloat(string name, float value)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Ints.Remove(name);
        Floats[name] = value;
    }

    public bool TryGetFloat(string name, out float value)
    {
        if (Floats.TryGetValue(name, out value))
            return true;
        if (Ints.TryGetValue(name, out int iv))
        {
            value = iv;
            return true;
        }
        value = 0f;
        return false;
    }

    public void SetInt(string name, int value)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Floats.Remove(name);
        Ints[name] = value;
    }

    public bool TryGetInt(string name, out int value)
    {
        if (Ints.TryGetValue(name, out value))
            return true;
        if (Floats.TryGetValue(name, out float fv))
        {
            value = (int)MathF.Round(fv);
            return true;
        }
        value = 0;
        return false;
    }

    public void SetVector(string name, SN.Vector4 value)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Vectors[name] = value;
    }

    public bool TryGetVector(string name, out SN.Vector4 value)
        => Vectors.TryGetValue(name, out value);

    public void SetColor(string name, Color value)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Colors[name] = value;
    }

    public bool TryGetColor(string name, out Color value)
        => Colors.TryGetValue(name, out value);

    public void SetTexture(string name, string? path, Texture2D? tex = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (string.IsNullOrWhiteSpace(path) && tex == null)
        {
            TexturePaths.Remove(name);
            Textures.Remove(name);
            return;
        }

        if (!string.IsNullOrWhiteSpace(path))
            TexturePaths[name] = path;
        else
            TexturePaths.Remove(name);

        Textures[name] = tex;
    }

    public bool TryGetTexture(string name, out Texture2D? tex)
        => Textures.TryGetValue(name, out tex);

    public bool TryGetTexturePath(string name, out string? path)
    {
        if (TexturePaths.TryGetValue(name, out string? p))
        {
            path = p;
            return true;
        }
        path = null;
        return false;
    }

    public bool Remove(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        bool removed = Floats.Remove(name);
        removed |= Ints.Remove(name);
        removed |= Vectors.Remove(name);
        removed |= Colors.Remove(name);
        removed |= TexturePaths.Remove(name);
        removed |= Textures.Remove(name);
        return removed;
    }

    public void Clear()
    {
        Floats.Clear();
        Ints.Clear();
        Vectors.Clear();
        Colors.Clear();
        TexturePaths.Clear();
        Textures.Clear();
    }

    public MaterialPropertyBag Clone()
    {
        var copy = new MaterialPropertyBag();
        copy.CopyFrom(this);
        return copy;
    }

    public void CopyFrom(MaterialPropertyBag other)
    {
        if (other == null) return;
        Clear();
        foreach (var kv in other.Floats) Floats[kv.Key] = kv.Value;
        foreach (var kv in other.Ints) Ints[kv.Key] = kv.Value;
        foreach (var kv in other.Vectors) Vectors[kv.Key] = kv.Value;
        foreach (var kv in other.Colors) Colors[kv.Key] = kv.Value;
        foreach (var kv in other.TexturePaths) TexturePaths[kv.Key] = kv.Value;
        foreach (var kv in other.Textures) Textures[kv.Key] = kv.Value;
    }

    /// <summary>
    /// Lazily resolves any <see cref="TexturePaths"/> whose runtime <see cref="Texture2D"/>
    /// has not yet been loaded. Safe to call repeatedly; already-resolved entries are skipped.
    /// </summary>
    public void ResolveTextures(string? projectRoot = null)
    {
        if (TexturePaths.Count == 0) return;

        string? root = projectRoot;
        if (string.IsNullOrWhiteSpace(root))
            root = ProjectService.Current?.RootPath;

        foreach (var kv in TexturePaths)
        {
            string key = kv.Key;
            string rawPath = kv.Value;
            if (string.IsNullOrWhiteSpace(rawPath)) continue;

            if (Textures.TryGetValue(key, out var existing) && existing != null)
                continue;

            string? resolved = ResolveTexturePath(rawPath, root);
            if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
                continue;

            try
            {
                Textures[key] = Texture2D.FromFile(resolved);
            }
            catch
            {
                // Leave unresolved so caller falls back to schema default texture
            }
        }
    }

    /// <summary>
    /// Populate this property bag from a <c>"properties"</c> or <c>"customProperties"</c> JSON object.
    /// Supports numbers (float/int), hex color strings (<c>#RRGGBB</c>/<c>#AARRGGBB</c>),
    /// <c>[x,y,z,w]</c> vector arrays, texture strings/objects (<c>{ "texture": "..." }</c>),
    /// and legacy <see cref="MaterialAsset"/> property objects.
    /// </summary>
    public void ReadFromJson(JsonElement propsEl, string? projectRoot = null)
    {
        if (propsEl.ValueKind != JsonValueKind.Object) return;

        foreach (var kv in propsEl.EnumerateObject())
        {
            string name = kv.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var el = kv.Value;

            switch (el.ValueKind)
            {
                case JsonValueKind.Number:
                {
                    string raw = el.GetRawText();
                    bool hasFloatChars = raw.IndexOf('.') >= 0 || raw.IndexOf('e') >= 0 || raw.IndexOf('E') >= 0;
                    if (!hasFloatChars && el.TryGetInt32(out int iv))
                        SetInt(name, iv);
                    else if (el.TryGetDouble(out double dv))
                        SetFloat(name, (float)dv);
                    break;
                }
                case JsonValueKind.String:
                {
                    string? s = el.GetString();
                    if (string.IsNullOrWhiteSpace(s)) break;
                    if (TryParseHexColor(s, out var col))
                        SetColor(name, col);
                    else
                        SetTexture(name, s, null);
                    break;
                }
                case JsonValueKind.Array:
                {
                    int len = el.GetArrayLength();
                    if (len >= 2)
                    {
                        float x = el[0].TryGetDouble(out double dx) ? (float)dx : 0f;
                        float y = el[1].TryGetDouble(out double dy) ? (float)dy : 0f;
                        float z = len >= 3 && el[2].TryGetDouble(out double dz) ? (float)dz : 0f;
                        float w = len >= 4 && el[3].TryGetDouble(out double dw) ? (float)dw : 0f;
                        SetVector(name, new SN.Vector4(x, y, z, w));
                    }
                    break;
                }
                case JsonValueKind.Object:
                {
                    // { "texture": "Assets/..." } or { "TexturePath": "..." } or { "path": "..." }
                    if (TryGetStringProp(el, "texture", out string? texPath)
                        || TryGetStringProp(el, "TexturePath", out texPath)
                        || TryGetStringProp(el, "path", out texPath))
                    {
                        if (!string.IsNullOrWhiteSpace(texPath))
                            SetTexture(name, texPath, null);
                        break;
                    }

                    // { "color": "#AARRGGBB" }
                    if (TryGetStringProp(el, "color", out string? colStr) && TryParseHexColor(colStr, out var cObj))
                    {
                        SetColor(name, cObj);
                        break;
                    }

                    // Legacy MaterialAsset format: { "Type": ..., "Floats": [...], "Int": ... }
                    if (el.TryGetProperty("Floats", out var flEl) && flEl.ValueKind == JsonValueKind.Array)
                    {
                        int len = flEl.GetArrayLength();
                        int propType = el.TryGetProperty("Type", out var tEl) && tEl.TryGetInt32(out int ti) ? ti : -1;
                        if (len == 1 && flEl[0].TryGetDouble(out double f0))
                        {
                            SetFloat(name, (float)f0);
                        }
                        else if (len >= 4)
                        {
                            float x = flEl[0].TryGetDouble(out double d0) ? (float)d0 : 0f;
                            float y = flEl[1].TryGetDouble(out double d1) ? (float)d1 : 0f;
                            float z = flEl[2].TryGetDouble(out double d2) ? (float)d2 : 0f;
                            float w = flEl[3].TryGetDouble(out double d3) ? (float)d3 : 1f;
                            if (propType == (int)ShaderPropType.Color)
                                SetColor(name, ColorFromNormalized(x, y, z, w));
                            else
                                SetVector(name, new SN.Vector4(x, y, z, w));
                        }
                        break;
                    }

                    if (el.TryGetProperty("Int", out var intEl) && intEl.TryGetInt32(out int legacyInt))
                    {
                        SetInt(name, legacyInt);
                        break;
                    }

                    break;
                }
            }
        }

        ResolveTextures(projectRoot);
    }

    /// <summary>
    /// Serialize all property overrides into a JSON object (without the enclosing property name).
    /// </summary>
    public void WriteToJson(Utf8JsonWriter jw)
    {
        jw.WriteStartObject();
        var written = new HashSet<string>(StringComparer.Ordinal);

        foreach (var kv in Colors)
        {
            if (written.Add(kv.Key))
                jw.WriteString(kv.Key, FormatHexColor(kv.Value));
        }

        foreach (var kv in Vectors)
        {
            if (!written.Add(kv.Key)) continue;
            jw.WritePropertyName(kv.Key);
            jw.WriteStartArray();
            jw.WriteNumberValue(kv.Value.X);
            jw.WriteNumberValue(kv.Value.Y);
            jw.WriteNumberValue(kv.Value.Z);
            jw.WriteNumberValue(kv.Value.W);
            jw.WriteEndArray();
        }

        foreach (var kv in TexturePaths)
        {
            if (string.IsNullOrWhiteSpace(kv.Value) || !written.Add(kv.Key)) continue;
            jw.WritePropertyName(kv.Key);
            jw.WriteStartObject();
            jw.WriteString("texture", kv.Value);
            jw.WriteEndObject();
        }

        foreach (var kv in Ints)
        {
            if (written.Add(kv.Key))
                jw.WriteNumber(kv.Key, kv.Value);
        }

        foreach (var kv in Floats)
        {
            if (written.Add(kv.Key))
                jw.WriteNumber(kv.Key, kv.Value);
        }

        jw.WriteEndObject();
    }

    public static string FormatHexColor(Color c)
        => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryParseHexColor(string? s, out Color c)
    {
        c = Avalonia.Media.Colors.White;
        if (string.IsNullOrWhiteSpace(s)) return false;
        string trimmed = s.Trim();
        if (trimmed[0] != '#') return false;
        string hex = trimmed.Substring(1);

        try
        {
            if (hex.Length == 8)
            {
                byte a = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte r = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte g = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte b = byte.Parse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                c = Color.FromArgb(a, r, g, b);
                return true;
            }
            if (hex.Length == 6)
            {
                byte r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                c = Color.FromArgb(255, r, g, b);
                return true;
            }
        }
        catch { }
        return false;
    }

    public static Color ColorFromNormalized(float r, float g, float b, float a = 1f)
    {
        static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return Color.FromArgb(ToByte(a), ToByte(r), ToByte(g), ToByte(b));
    }

    private static bool TryGetStringProp(JsonElement obj, string propName, out string? value)
    {
        if (obj.TryGetProperty(propName, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString();
            return true;
        }
        value = null;
        return false;
    }

    private static string? ResolveTexturePath(string relOrAbs, string? projectRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(relOrAbs)) return null;
            if (Path.IsPathRooted(relOrAbs))
            {
                string abs = Path.GetFullPath(relOrAbs);
                return File.Exists(abs) ? abs : AssetPathRemap.FindExisting(relOrAbs);
            }

            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                string combined = Path.GetFullPath(Path.Combine(projectRoot, relOrAbs));
                if (File.Exists(combined)) return combined;
            }

            return AssetPathRemap.FindExisting(relOrAbs);
        }
        catch
        {
            return null;
        }
    }
}
