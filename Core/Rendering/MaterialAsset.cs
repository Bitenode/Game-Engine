using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Game_Engine.Core.Rendering
{
    public sealed class MaterialPropertyValue
    {
        public ShaderPropType Type;
        public float[] Floats;      // 1..4 for Float/Range/Vector/Color (RGBA)
        public int Int;
        public bool Bool;
        public string TexturePath;  // project-relative for Texture2D
    }

    /// <summary>
    /// .material asset: references a ShaderAsset and stores values by property name.
    /// </summary>
    public sealed class MaterialAsset
    {
        public string Name;
        public string ShaderPath;  // project-relative path to .shader
        public Dictionary<string, MaterialPropertyValue> Properties = new Dictionary<string, MaterialPropertyValue>(StringComparer.Ordinal);

        public static MaterialAsset Load(string absPath)
        {
            var json = File.ReadAllText(absPath);
            return JsonSerializer.Deserialize<MaterialAsset>(json);
        }

        public static void Save(string absPath, MaterialAsset m)
        {
            var json = JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(absPath, json);
        }
    }
}
