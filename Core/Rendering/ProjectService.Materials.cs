using System;
using System.Collections.Generic;
using System.IO;
using Game_Engine.Core.Rendering;

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
