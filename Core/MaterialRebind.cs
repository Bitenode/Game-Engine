#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game_Engine.Core.Component;

namespace Game_Engine.Core
{
    /// <summary>
    /// Scene material warm-up (one-shot probe + short grace window).
    /// Fix: after Load/Play, MeshRenderers could have null Material(s) even when paths exist,
    /// so pre-Z draws but color pass shows nothing until Inspector touches the object.
    ///
    /// This rebinder:
    ///  - Scans MeshRenderers and, when Material/Materials are null but paths exist, loads them.
    ///  - Always calls ResolveMaterials() to rebuild internal slot/target caches.
    ///  - Runs once after any SceneService.Changed and also for a few frames after that
    ///    (covers async-ish property init that may complete on the next frame).
    /// </summary>
    public static class MaterialRebind
    {
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Triggers a few follow-up frames of probing after scene changes.
        // This keeps the per-frame work tiny but robust.
        static volatile int s_framesToProbe = 0;  // decremented by RepairScene()
        static volatile bool s_forceOnce = true;  // first call after app start / play

        static MaterialRebind()
        {
            try
            {
                SceneService.Changed += () =>
                {
                    s_framesToProbe = 8;   // probe ~8 frames after any scene graph/material change
                    s_forceOnce = true;    // ensure at least one full pass
                };
            }
            catch { /* best effort */ }
        }

        /// <summary>Force a rebind on the next RepairScene call.</summary>
        public static void MarkDirty()
        {
            s_forceOnce = true;
            if (s_framesToProbe < 4) s_framesToProbe = 4;
        }

        /// <summary>True while we still have follow-up frames to probe. Callers
        /// can use this to schedule additional renders (e.g. InvalidateVisual).</summary>
        public static bool NeedsMoreFrames => s_forceOnce || s_framesToProbe > 0;

        /// <summary>
        /// Call this once per frame from GameView.Render (already present in your file).
        /// It’s fast when nothing needs doing.
        /// </summary>
        public static void RepairScene()
        {
            // Quick-out if we have nothing to do.
            if (!s_forceOnce && s_framesToProbe <= 0)
                return;

            bool anyTouched = false;

            try
            {
                var root = SceneService.Root;
                for (int i = 0; i < root.Count; i++)
                    anyTouched |= Walk(root[i]);
            }
            catch { /* swallow inside render loop */ }

            // We never spam SceneService.NotifyChanged() here—
            // writes go straight into components used by the renderer.

            // Hysteresis: keep probing for a few frames even if we didn't touch anything this time.
            if (s_framesToProbe > 0) s_framesToProbe--;
            s_forceOnce = false;
        }

        // Walk returns true if anything changed
        static bool Walk(GameObject go)
        {
            bool changed = false;

            // Skip entire "Grass" container but allow texture warm-up for individual grass GOs
            if (go.Name == "Grass")
            {
                // Still walk children so their textures get warmed up
                var grassCh = go.Children;
                for (int gc = 0; gc < grassCh.Count; gc++)
                    changed |= Walk(grassCh[gc]);
                return changed;
            }

            var bs = go.Behaviors;

            // Skip MeshRenderers that belong to components with their own material management
            bool hasDecal = false;
            bool hasParticle = false;
            for (int i = 0; i < bs.Count; i++)
            {
                if (bs[i] is Component.Decal) hasDecal = true;
                if (bs[i] is Component.ParticleEmitter) hasParticle = true;
            }

            for (int i = 0; i < bs.Count; i++)
            {
                var mr = bs[i] as MeshRenderer;
                if (mr != null && !hasDecal && !hasParticle)
                    changed |= RebindRenderer(mr);
            }

            var ch = go.Children;
            for (int c = 0; c < ch.Count; c++)
                changed |= Walk(ch[c]);

            return changed;
        }

        static bool RebindRenderer(MeshRenderer mr)
        {
            bool changed = false;

            // Single 'Material' from 'MaterialPath' (if present) -------------
            if (mr.Material == null)
            {
                string rel = GetStringMember(mr, "MaterialPath");
                if (!string.IsNullOrWhiteSpace(rel))
                {
                    try
                    {
                        var m = ProjectService.MaterialsLoad(rel);
                        if (m != null) { mr.Material = m; changed = true; }
                    }
                    catch { /* ignore bad path */ }
                }
            }

            // Materials list from 'MaterialPaths' (if present) ----------------
            IList paths = GetListMember(mr, "MaterialPaths");
            IList mats = GetListMember(mr, "ResolvedMaterials", createIfMissing: paths != null);

            if (paths != null && mats != null)
            {
                // Ensure 'mats' has at least paths.Count items
                for (int i = 0; i < paths.Count; i++)
                {
                    string rel = paths[i] as string;
                    Core.Material m = null;
                    if (!string.IsNullOrWhiteSpace(rel))
                    {
                        try { m = ProjectService.MaterialsLoad(rel); } catch { m = null; }
                    }

                    if (i < mats.Count)
                    {
                        if (!object.ReferenceEquals(mats[i], m)) { mats[i] = m; changed = true; }
                    }
                    else { mats.Add(m); changed = true; }

                    // If primary Material is still null, set a reasonable default
                    if (mr.Material == null && m != null) { mr.Material = m; changed = true; }
                }
            }

            //         Warm up texture slots: if a RuntimeTexSlot has a SourcePath but
            //         its Texture is null, try to load from disk now (deferred retry
            //         for textures that couldn't be loaded during initial scene load). ---
            try { changed |= WarmUpMaterialTextures(mr.Material); } catch { }

            // Also warm up textures for all resolved materials (multi-submesh support)
            if (mr.ResolvedMaterials != null)
            {
                for (int ri = 0; ri < mr.ResolvedMaterials.Count; ri++)
                {
                    try { changed |= WarmUpMaterialTextures(mr.ResolvedMaterials[ri]); } catch { }
                }
            }

            // Resolve internal slots no matter what (cheap and idempotent) ---
            try { mr.ResolveMaterials(); } catch { }

            return changed;
        }

        /// <summary>
        /// Iterate a Material's Textures list and retry loading from SourcePath
        /// for any RuntimeTexSlot whose Texture is still null.
        /// </summary>
        static bool WarmUpMaterialTextures(Core.Material mat)
        {
            if (mat == null) return false;
            var textures = mat.Textures;
            if (textures == null || textures.Count == 0) return false;

            bool any = false;
            for (int i = 0; i < textures.Count; i++)
            {
                var slot = textures[i];
                if (slot == null) continue;

                // Only handle RuntimeTexSlot (most common for imported models)
                string srcPath = null;
                Texture2D curTex = null;
                try
                {
                    var t = slot.GetType();
                    var pTex = t.GetProperty("Texture", BF);
                    var pSrc = t.GetProperty("SourcePath", BF);
                    if (pTex != null) curTex = pTex.GetValue(slot) as Texture2D;
                    if (pSrc != null) srcPath = pSrc.GetValue(slot) as string;

                    if (curTex != null || string.IsNullOrWhiteSpace(srcPath))
                        continue; // already loaded or no path to try

                    // Resolve to absolute — try project root, then Assets sub-folder
                    string abs = srcPath;
                    if (!System.IO.Path.IsPathRooted(abs))
                    {
                        var proj = ProjectService.Current;
                        if (proj != null)
                        {
                            abs = System.IO.Path.Combine(proj.RootPath, srcPath);
                            if (!System.IO.File.Exists(abs))
                                abs = System.IO.Path.Combine(proj.AssetsPath, srcPath);
                        }
                    }
                    if (string.IsNullOrWhiteSpace(abs) || !System.IO.File.Exists(abs))
                    {
                        // Last resort: search by filename inside Assets
                        var proj2 = ProjectService.Current;
                        if (proj2 != null && System.IO.Directory.Exists(proj2.AssetsPath))
                        {
                            try
                            {
                                var found = System.IO.Directory.GetFiles(proj2.AssetsPath,
                                    System.IO.Path.GetFileName(srcPath), System.IO.SearchOption.AllDirectories);
                                if (found.Length > 0) abs = found[0];
                            }
                            catch { }
                        }
                        if (string.IsNullOrWhiteSpace(abs) || !System.IO.File.Exists(abs))
                            continue;
                    }

                    var loaded = Texture2D.FromFile(abs);
                    if (loaded != null && pTex.CanWrite)
                    {
                        pTex.SetValue(slot, loaded);
                        any = true;
                    }
                }
                catch { /* best-effort */ }
            }
            return any;
        }

        // ----- Reflection helpers (C# 7.3 compatible) ------------------------------

        static string GetStringMember(object obj, string name)
        {
            try
            {
                var t = obj.GetType();
                var p = t.GetProperty(name, BF);
                if (p != null && p.PropertyType == typeof(string))
                    return p.GetValue(obj) as string;

                var f = t.GetField(name, BF);
                if (f != null && f.FieldType == typeof(string))
                    return f.GetValue(obj) as string;
            }
            catch { }
            return null;
        }

        static IList GetListMember(object obj, string name, bool createIfMissing = false)
        {
            try
            {
                var t = obj.GetType();

                // Try property first
                var p = t.GetProperty(name, BF);
                if (p != null)
                {
                    var cur = p.GetValue(obj) as IList;
                    if (cur == null && createIfMissing)
                    {
                        var listType = p.PropertyType;
                        try { cur = Activator.CreateInstance(listType) as IList; } catch { cur = null; }
                        if (cur != null) { try { p.SetValue(obj, cur); } catch { } }
                    }
                    return cur;
                }

                // Then field
                var f = t.GetField(name, BF);
                if (f != null)
                {
                    var cur = f.GetValue(obj) as IList;
                    if (cur == null && createIfMissing)
                    {
                        var listType = f.FieldType;
                        try { cur = Activator.CreateInstance(listType) as IList; } catch { cur = null; }
                        if (cur != null) { try { f.SetValue(obj, cur); } catch { } }
                    }
                    return cur;
                }
            }
            catch { }
            return null;
        }
    }
}
