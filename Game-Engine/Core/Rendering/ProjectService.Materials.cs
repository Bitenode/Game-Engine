using Assimp;
using Avalonia.Media;
using Game_Engine.Core.Rendering;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Game_Engine.Core
{
    public static partial class ProjectService
    {
        private static readonly Dictionary<string, ShaderAsset> s_shaderCache = new Dictionary<string, ShaderAsset>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, MaterialAsset> s_matCache = new Dictionary<string, MaterialAsset>(StringComparer.OrdinalIgnoreCase);


        public static ShaderAsset LoadShaderAsset(string rel)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rel)) return null;
                ShaderAsset cached;
                if (s_shaderCache.TryGetValue(rel, out cached)) return cached;

                var abs = ToAbsolute(rel);
                if (!File.Exists(abs)) return null;
                var sa = ShaderAsset.Load(abs);
                s_shaderCache[rel] = sa;
                return sa;
            }
            catch { return null; }
        }

        public static MaterialAsset LoadMaterialAsset(string rel)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rel)) return null;
                MaterialAsset cached;
                if (s_matCache.TryGetValue(rel, out cached)) return cached;

                var abs = ToAbsolute(rel);
                if (!File.Exists(abs)) return null;
                var ma = MaterialAsset.Load(abs);
                s_matCache[rel] = ma;
                return ma;
            }
            catch { return null; }
        }

        // Build a runtime Material from a project-relative or absolute .material path.
        public static Material MaterialsLoad(string relOrAbs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(relOrAbs) || Current == null) return null;

                // Normalize to absolute
                string abs;
                var root = Path.GetFullPath(Current.RootPath);
                abs = Path.IsPathRooted(relOrAbs)
                    ? Path.GetFullPath(relOrAbs)
                    : Path.GetFullPath(Path.Combine(root, relOrAbs));

                if (!File.Exists(abs)) return null;

                System.Diagnostics.Debug.WriteLine($"[MatTrace:MatLoad] request='{relOrAbs}' -> abs='{abs}'");

                var json = File.ReadAllText(abs);
                using var doc = JsonDocument.Parse(json);
                var rootEl = doc.RootElement;

                // ---------- construct runtime Material ----------
                var m = new Material();
                m.Name = rootEl.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "Material") : "Material";
                System.Diagnostics.Debug.WriteLine($"[MatTrace:MatLoad] name='{m.Name}', shader='(none)'");

                // ---------- parameters ----------
                if (rootEl.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    // Tint
                    if (p.TryGetProperty("Tint", out var tintEl))
                    {
                        if (tintEl.ValueKind == JsonValueKind.String)
                        {
                            if (TryParseHexColor(tintEl.GetString(), out var col))
                            {
                                m.BaseColor = col;
                                System.Diagnostics.Debug.WriteLine($"[MatTrace:MatLoad] param.Tint(hex) = {tintEl.GetString()}");
                            }
                        }
                        else if (tintEl.ValueKind == JsonValueKind.Array && tintEl.GetArrayLength() >= 4)
                        {
                            float r = (float)tintEl[0].GetDouble();
                            float g = (float)tintEl[1].GetDouble();
                            float b = (float)tintEl[2].GetDouble();
                            float a = (float)tintEl[3].GetDouble();
                            m.BaseColor = Color.FromArgb(ToByte(a * 255f), ToByte(r * 255f), ToByte(g * 255f), ToByte(b * 255f));
                            System.Diagnostics.Debug.WriteLine($"[MatTrace:MatLoad] param.Tint(array) = [{r},{g},{b},{a}]");
                        }
                    }

                    // Metallic
                    if (p.TryGetProperty("Metallic", out var metEl) && metEl.TryGetDouble(out var md))
                    {
                        m.Metallic = Clamp01((float)md);
                        System.Diagnostics.Debug.WriteLine($"[MatTrace:MatLoad] param.Metallic = {m.Metallic}");
                    }

                    // Roughness -> Smoothness (alias maintained in Material)
                    if (p.TryGetProperty("Roughness", out var rEl) && rEl.TryGetDouble(out var rd))
                    {
                        m.Smoothness = Clamp01(1f - (float)rd);
                        System.Diagnostics.Debug.WriteLine($"[MatTrace:MatLoad] param.Roughness = {(float)rd} -> Smoothness = {m.Smoothness}");
                    }
                    if (p.TryGetProperty("Smoothness", out var sEl) && sEl.TryGetDouble(out var sd))
                    {
                        m.Smoothness = Clamp01((float)sd);
                    }

                    // Transparent
                    if (p.TryGetProperty("Transparent", out var tEl))
                    {
                        m.Transparent = tEl.ValueKind == JsonValueKind.True;
                        System.Diagnostics.Debug.WriteLine($"[MatTrace:MatLoad] param.Transparent = {m.Transparent}");
                    }

                    // AlphaCutoff
                    if (p.TryGetProperty("AlphaCutoff", out var acEl) && acEl.TryGetDouble(out var acd))
                    {
                        m.AlphaCutoff = Clamp01((float)acd);
                        System.Diagnostics.Debug.WriteLine($"[MatTrace:MatLoad] param.AlphaCutoff = {m.AlphaCutoff}");
                    }
                }

                // ---------- textures -> RuntimeTexSlot list ----------
                // support both: 
                //  "textures": { "Albedo":"Assets/...", "Roughness":"...", "AmbientOcclusion":"..." }
                //  "textures": [ { "usage":"Albedo", "path":"Assets/..." }, ... ]
                if (rootEl.TryGetProperty("textures", out var tx))
                {
                    System.Diagnostics.Debug.WriteLine("[MatTrace:MatLoad] textures found, kind=" + tx.ValueKind);

                    void AddSlot(string usage, string rawPath)
                    {
                        if (string.IsNullOrWhiteSpace(rawPath)) return;
                        string absTex = ResolveToProjectAbs(rawPath);
                        bool exists = File.Exists(absTex);
                        System.Diagnostics.Debug.WriteLine($"[MatTrace:MatLoad] add '{usage}' raw='{rawPath}' -> abs='{absTex}' exists={exists}");
                        if (!exists) return;

                        try
                        {
                            var tex = Texture2D.FromFile(absTex);
                            var slot = new RuntimeTexSlot
                            {
                                Usage = NormalizeUsage(usage),
                                Texture = tex,
                                FaceMask = -1,
                                NoFlipV = false,
                                SourcePath = rawPath  // preserve for scene serialization
                            };
                            m.Textures.Add(slot);
                            System.Diagnostics.Debug.WriteLine($"[MatTrace:MatLoad]   +slot '{slot.Usage}' ({tex.Width}x{tex.Height})");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MatTrace:MatLoad]   !failed to load '{absTex}': {ex.Message}");
                        }
                    }

                    if (tx.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var kv in tx.EnumerateObject())
                        {
                            var key = kv.Name;
                            var raw = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() : null;
                            if (string.IsNullOrWhiteSpace(raw)) continue;
                            AddSlot(key, raw);
                        }
                    }
                    else if (tx.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in tx.EnumerateArray())
                        {
                            if (el.ValueKind != JsonValueKind.Object) continue;
                            string usage = el.TryGetProperty("usage", out var uEl) ? (uEl.GetString() ?? "") : "";
                            string raw = el.TryGetProperty("path", out var pEl) ? (pEl.GetString() ?? "") : "";
                            if (string.IsNullOrWhiteSpace(raw)) continue;
                            AddSlot(usage, raw);
                        }
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[MatTrace:MatLoad] textures: (none)");
                }

                // ---------- sanity defaults ----------
                if (m.BaseColor.A == 0 && m.BaseColor.R == 0 && m.BaseColor.G == 0 && m.BaseColor.B == 0)
                    m.BaseColor = Colors.White;
                if (m.Smoothness < 0f) m.Smoothness = 0.5f;

                m.ShaderAssetPath = null;
                m.Lit = true;

                System.Diagnostics.Debug.WriteLine($"[MatTrace:MatLoad] result: BaseColor={m.BaseColor}, Metallic={m.Metallic}, Smoothness={m.Smoothness}, Transparent={m.Transparent}, AlphaCutoff={m.AlphaCutoff}");
                System.Diagnostics.Debug.WriteLine($"[MatTrace:MatLoad] slots added: {m.Textures.Count}");

                return m;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MatTrace:MatLoad] ERROR " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }

            // ---- local helpers ----
            static string ResolveToProjectAbs(string relOrAbsPath)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(relOrAbsPath)) return relOrAbsPath;
                    if (Path.IsPathRooted(relOrAbsPath)) return Path.GetFullPath(relOrAbsPath);
                    if (Current == null) return relOrAbsPath;
                    var root = Path.GetFullPath(Current.RootPath);
                    return Path.GetFullPath(Path.Combine(root, relOrAbsPath));
                }
                catch { return relOrAbsPath; }
            }

            static string NormalizeUsage(string u)
            {
                if (string.IsNullOrWhiteSpace(u)) return "Albedo";
                u = u.Trim().ToLowerInvariant();
                if (u.Contains("basecolor") || u.Contains("base") || u.Contains("albedo") || u.Contains("_maintex") || u.Contains("_basemap")) return "Albedo";
                if (u.Contains("normal")) return "Normal";
                if (u.Contains("rough") || u.Contains("smooth")) return "Roughness";
                if (u.Contains("metal")) return "Metallic";
                if (u.Contains("ambientocclusion") || u == "ao" || u.Contains("occl")) return "AmbientOcclusion";
                if (u.Contains("emiss")) return "Emissive";
                if (u.Contains("opacity") || u.Contains("alpha") || u.Contains("transp")) return "Opacity";
                if (u.Contains("spec")) return "Specular";
                return "Albedo";
            }
        }





        static float Clamp01(float v) { if (v < 0f) return 0f; if (v > 1f) return 1f; return v; }
        static byte ToByte(float v) { if (v < 0f) v = 0f; if (v > 255f) v = 255f; return (byte)(v + 0.5f); }

        static bool TryParseHexColor(string s, out Color c)
        {
            c = Colors.White;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s[0] == '#') s = s.Substring(1);

            // ARGB (8) or RGB (6)
            try
            {
                if (s.Length == 8)
                {
                    byte a = byte.Parse(s.Substring(0, 2), NumberStyles.HexNumber);
                    byte r = byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber);
                    byte g = byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber);
                    byte b = byte.Parse(s.Substring(6, 2), NumberStyles.HexNumber);
                    c = Color.FromArgb(a, r, g, b);
                    return true;
                }
                if (s.Length == 6)
                {
                    byte r = byte.Parse(s.Substring(0, 2), NumberStyles.HexNumber);
                    byte g = byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber);
                    byte b = byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber);
                    c = Color.FromArgb(255, r, g, b);
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static string CreateNewMaterial(string name, string shaderRel, string folderRel = "Assets/Materials")
        {
            Directory.CreateDirectory(ToAbsolute(folderRel));

            var mat = new MaterialAsset();
            mat.Name = name;
            mat.ShaderPath = shaderRel;
            // seed defaults for Unlit/Color
            mat.Properties["_BaseColor"] = new MaterialPropertyValue
            {
                Type = ShaderPropType.Color,
                Floats = new float[] { 1f, 1f, 1f, 1f }
            };

            var rel = Path.Combine(folderRel, Safe(name) + ".material").Replace('\\', '/');
            MaterialAsset.Save(ToAbsolute(rel), mat);
            TouchModified();
            return rel;
        }

        public static string CreateNewUnlitColorShader(string name = "UnlitColor", string folderRel = "Assets/Shaders")
        {
            Directory.CreateDirectory(ToAbsolute(folderRel));
            var sa = new ShaderAsset();
            sa.Name = name;
            sa.Technique = "Unlit/Color";
            sa.Properties.Add(new ShaderPropertyDecl { Name = "_BaseColor", Type = ShaderPropType.Color, Default = new float[] { 1, 1, 1, 1 }, Tooltip = "Color (RGBA)" });
            sa.Properties.Add(new ShaderPropertyDecl { Name = "_MainTex", Type = ShaderPropType.Texture2D, DefaultTexture = null, Tooltip = "Optional base texture" });

            var rel = Path.Combine(folderRel, Safe(name) + ".shader").Replace('\\', '/');
            ShaderAsset.Save(ToAbsolute(rel), sa);
            TouchModified();
            return rel;
        }

        public static string CreateNewStandardShader(string name = "StandardLit", string folderRel = "Assets/Shaders")
        {
            Directory.CreateDirectory(ToAbsolute(folderRel));
            var sa = new ShaderAsset();
            sa.Name = name;
            sa.Technique = "Lit/Standard";
            sa.Properties.Add(new ShaderPropertyDecl { Name = "_BaseColor", Type = ShaderPropType.Color, Default = new float[] { 1, 1, 1, 1 }, Tooltip = "Albedo tint (RGBA)" });
            sa.Properties.Add(new ShaderPropertyDecl { Name = "_BaseMap", Type = ShaderPropType.Texture2D, Tooltip = "Albedo/Base texture" });
            sa.Properties.Add(new ShaderPropertyDecl { Name = "_Metallic", Type = ShaderPropType.Range, Min = 0, Max = 1, Default = new float[] { 0 } });
            sa.Properties.Add(new ShaderPropertyDecl { Name = "_Smoothness", Type = ShaderPropType.Range, Min = 0, Max = 1, Default = new float[] { 0.5f } });
            sa.Properties.Add(new ShaderPropertyDecl { Name = "_NormalMap", Type = ShaderPropType.Texture2D });
            sa.Properties.Add(new ShaderPropertyDecl { Name = "_AOMap", Type = ShaderPropType.Texture2D });

            var rel = Path.Combine(folderRel, Safe(name) + ".shader").Replace('\\', '/');
            ShaderAsset.Save(ToAbsolute(rel), sa);
            TouchModified();
            return rel;
        }

        private static string ToAbsolute(string projectRelative)
        {
            var proj = Current;
            if (proj == null) return Path.GetFullPath(projectRelative);
            if (Path.IsPathRooted(projectRelative)) return projectRelative;
            return Path.GetFullPath(Path.Combine(proj.RootPath, projectRelative));
        }

        private static string Safe(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "New";
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char ch = chars[i];
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == ' ')) chars[i] = '_';
            }
            return new string(chars).Trim();
        }
    }
}
