using System;

namespace Game_Engine.Core
{
    /// <summary>Skip this property in the default inspector property list.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
    public sealed class HideInInspectorAttribute : Attribute { }

    /// <summary>Draw a numeric inspector field as a slider between <see cref="Min"/> and <see cref="Max"/>.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
    public sealed class RangeAttribute : Attribute
    {
        public double Min { get; }
        public double Max { get; }

        public RangeAttribute(double min, double max)
        {
            Min = min;
            Max = max;
        }

        public RangeAttribute(float min, float max) : this((double)min, (double)max) { }

        public RangeAttribute(int min, int max) : this((double)min, (double)max) { }
    }

    /// <summary>Preset used by <see cref="AssetPathAttribute"/> when extensions are not listed explicitly.</summary>
    public enum AssetPathKind
    {
        Custom = 0,
        Audio = 1,
        Image = 2,
        ModelOrImage = 3,
    }

    /// <summary>
    /// Draw a string property as an Import / drop-zone asset path.
    /// Use <see cref="AssetPathKind"/> for common filters, or pass extensions like <c>.wav</c>, <c>.png</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class AssetPathAttribute : Attribute
    {
        public AssetPathKind Kind { get; }
        public string[] Extensions { get; }
        public string? Label { get; set; }
        public string? DialogTitle { get; set; }
        public string? Watermark { get; set; }
        public string? DropHint { get; set; }
        public string? FilterName { get; set; }

        public AssetPathAttribute(AssetPathKind kind)
        {
            Kind = kind;
            Extensions = Array.Empty<string>();
        }

        public AssetPathAttribute(params string[] extensions)
        {
            Kind = AssetPathKind.Custom;
            Extensions = extensions ?? Array.Empty<string>();
        }
    }
}
