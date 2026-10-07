#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Biome.Graph;
using Game_Engine.Core.Importers;
using SN = System.Numerics;

namespace Game_Engine.Core.Component;

/// <summary>
/// Places biome-graph fauna on the crust and walks them with the pack's idle/walk clips.
/// Species Id resolves to Assets/Animals/Models/{Species}_Rig.fbx.
/// </summary>
[ComponentCategory("Environment")]
public sealed class PlanetFaunaTableBehavior : Behavior
{
    FaunaLayerRecipe[] _layers = Array.Empty<FaunaLayerRecipe>();
    readonly List<HerdAnimal> _herd = new();
    readonly Dictionary<string, SpeciesAssets> _species = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    GameObject? _root;
    float _spawnWait;
    bool _loggedIdle;
    PlanetTerrain? _terrain;
    RigidbodyPlayer? _player;
    float _playerSearchWait;

    public FaunaLayerRecipe[] Layers => _layers;

    public void Bind(FaunaLayerRecipe[]? layers)
    {
        _layers = layers ?? Array.Empty<FaunaLayerRecipe>();
        ClearHerd();
        _spawnWait = 0f;
        _loggedIdle = false;
        if (_layers.Length > 0)
            Log.Info($"[Fauna] Bound {_layers.Length} layer(s): {string.Join(", ", _layers.Select(l => l?.SpeciesId ?? "?"))}.");
    }

    public float HerdSpacingOrDefault(int index, float fallback = 18f)
    {
        if (index < 0 || index >= _layers.Length) return fallback;
        float s = _layers[index].HerdSpacing;
        return s > 0.5f ? s : fallback;
    }

    public override void OnEnable()
    {
        base.OnEnable();
        if (gameObject == null) return;

        // Graph apply used to add a second copy before the saved component was restored.
        // Keep the earliest one on this object and drop the rest.
        var behaviors = gameObject.Behaviors;
        for (int i = 0; i < behaviors.Count; i++)
        {
            if (behaviors[i] is not PlanetFaunaTableBehavior existing) continue;
            if (ReferenceEquals(existing, this)) break;
            gameObject.RemoveBehavior(this);
            return;
        }

        var layers = GetComponent<PlanetTerrain>()?.CompiledFaunaLayers;
        if (layers != null)
            Bind(layers);
    }

    public override void OnDisable() => ClearHerd();

    public override void OnDestroy() => ClearHerd();

    public override void Update()
    {
        if (!SceneService.PlayMode || _layers.Length == 0) return;
        if (_terrain == null || _terrain.gameObject != gameObject)
            _terrain = GetComponent<PlanetTerrain>();
        var terrain = _terrain;
        if (terrain == null || terrain.Config == null) return;

        float dt = Math.Max(0f, Time.deltaTime);
        for (int i = _herd.Count - 1; i >= 0; i--)
        {
            var animal = _herd[i];
            if (animal.Go == null || animal.Go.Parent == null)
            {
                _herd.RemoveAt(i);
                continue;
            }
            animal.Go.Enabled = true;
            StepAnimal(terrain, animal, dt);
        }

        _playerSearchWait -= dt;
        _spawnWait -= dt;
        if (_spawnWait > 0f) return;
        _spawnWait = 0.35f;
        TrySpawnOne(terrain);
    }

    void TrySpawnOne(PlanetTerrain terrain)
    {
        // Counting the herd is cheap. The player lookup and spot search are not,
        // so only pay for them when a layer still wants an animal it can load.
        bool any = false;
        bool anchored = false;
        var center = SN.Vector3.Zero;
        var camDir = SN.Vector3.UnitY;
        foreach (var layer in _layers)
        {
            if (layer == null || layer.Density <= 0.001f) continue;
            string species = (layer.SpeciesId ?? "").Trim();
            if (species.Length == 0) continue;
            any = true;
            if (_missing.Contains(species)) continue;

            int want = Math.Clamp((int)MathF.Round(Math.Max(layer.Density, 0.2f) * 30f), 2, 6);
            int have = 0;
            for (int i = 0; i < _herd.Count; i++)
                if (string.Equals(_herd[i].Species, species, StringComparison.OrdinalIgnoreCase))
                    have++;
            if (have >= want) continue;
            if (ResolveSpecies(species) == null) continue;

            if (!anchored)
            {
                center = terrain.GetWorldCenter();
                if (!TryPlayerAnchor(terrain, center, out var anchor))
                {
                    LogIdle("no player position yet");
                    return;
                }
                camDir = SN.Vector3.Normalize(anchor - center);
                anchored = true;
            }

            float spacing = layer.HerdSpacing > 0.5f ? layer.HerdSpacing : 18f;
            if (!TryPickSpot(terrain, camDir, center, species, spacing, layer, out var dir, out var pos, out var biomeName))
                continue;

            if (!TrySpawn(terrain, layer, species, dir, pos))
                continue;
            Log.Info($"[Fauna] {species} on {biomeName}.");
            return;
        }
        if (!any)
            LogIdle("graph has no fauna layers");
    }

    bool TryPlayerAnchor(PlanetTerrain terrain, SN.Vector3 center, out SN.Vector3 anchor)
    {
        anchor = default;
        // A scene-wide behavior search walks every rig bone up to the root.
        // Keep the player we found and only look again if it goes away.
        if (_player == null || _player.gameObject == null || !_player.IsActiveAndEnabled)
        {
            _player = null;
            if (_playerSearchWait <= 0f)
            {
                _playerSearchWait = 2f;
                _player = SceneQuery.FindBehaviors<RigidbodyPlayer>().FirstOrDefault(p => p?.gameObject != null);
            }
        }
        if (_player?.gameObject != null)
        {
            var pos = SceneGraphUtil.AccumulateWorld(_player.gameObject).Translation;
            if ((pos - center).LengthSquared() > 16f)
            {
                anchor = pos;
                return true;
            }
        }
        anchor = terrain.LastCameraPosition;
        return (anchor - center).LengthSquared() > 16f;
    }

    bool TryPickSpot(PlanetTerrain terrain, SN.Vector3 camDir, SN.Vector3 center, string species,
        float spacing, FaunaLayerRecipe layer, out SN.Vector3 dir, out SN.Vector3 pos, out string biomeName)
    {
        dir = camDir;
        pos = default;
        biomeName = "";
        float stand = terrain.SampleStandWorldRadius(camDir);
        if (stand < 1f)
        {
            LogIdle("heightfield unavailable");
            return false;
        }

        string last = "";
        bool wantNote = !_loggedIdle && _herd.Count == 0;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            float ang = (Hash01(species, _herd.Count * 17 + attempt * 13) * 2f - 1f) * MathF.PI;
            float dist = attempt == 0 ? 8f : Math.Clamp(4f + Hash01(species, 90 + attempt) * 28f, 4f, 36f);
            var tangent = Tangent(camDir, ang);
            dir = SN.Vector3.Normalize(camDir + tangent * (dist / stand));
            if (!TryReadSurface(terrain, dir, wantNote, out var biome, out _, out last))
                continue;
            biomeName = biome?.Name ?? "land";
            if (biome != null && !AllowsBiome(layer, biome))
                continue;
            if (biome == null && !string.Equals(species, "Deer", StringComparison.OrdinalIgnoreCase))
                continue;
            float ground = terrain.SampleStandWorldRadius(dir);
            if (ground < 1f) continue;
            pos = center + dir * ground;
            if (TooClose(species, pos, Math.Min(spacing, 12f))) continue;
            return true;
        }
        LogIdle($"no land spot for {species} ({last})");
        return false;
    }

    /// <summary>
    /// Same land test the vegetation spawner uses: heightfield vs sea, biome from
    /// normalized altitude. The world-position blend crushed near-shore land into Ocean.
    /// </summary>
    static bool TryReadSurface(PlanetTerrain terrain, SN.Vector3 dir, bool wantNote, out BiomeDefinition? biome, out float worldR, out string note)
    {
        biome = null;
        note = "";
        worldR = terrain.SampleStandWorldRadius(dir);
        if (terrain.Config == null)
        {
            note = "no planet config";
            return false;
        }
        float localR = terrain.WorldToLocalLength(worldR);
        float sea = terrain.Config.SeaLevel;
        if (localR < sea + 0.15f)
        {
            if (wantNote) note = $"below sea {localR:F1} < {sea:F1}";
            return false;
        }
        float height = localR - terrain.Config.Radius;
        float alt = terrain.Map?.NormalizeAltitude(height) ?? -1f;
        biome = terrain.Map?.GetDominantBiome(dir, alt);
        if (wantNote) note = $"{biome?.Name ?? "none"} h={height:F1} alt={alt:F2}";
        return true;
    }

    void LogIdle(string reason)
    {
        if (_loggedIdle || _herd.Count > 0) return;
        _loggedIdle = true;
        Log.Info($"[Fauna] Not spawned yet: {reason}. Layers={_layers.Length}.");
    }

    bool TrySpawn(PlanetTerrain terrain, FaunaLayerRecipe layer, string species, SN.Vector3 dir, SN.Vector3 pos)
    {
        var assets = ResolveSpecies(species);
        if (assets == null) return false;

        GameObject model;
        try
        {
            model = ModelImporter.ImportModel(assets.ModelPath);
        }
        catch (Exception ex)
        {
            Log.Warning($"[Fauna] {species} import failed: {ex.Message}");
            _missing.Add(species);
            return false;
        }

        PruneEmptyRigNodes(model);
        var host = new GameObject(species);
        EnsureRoot();
        _root!.AddChild(host);
        host.AddChild(model);
        float scale = BodyScale(species);
        model.Transform.Scale = new Vector3(scale, scale, scale);
        model.Transform.Position = new Vector3(0, 0, 0);
        PlantFeet(model);
        ApplySpeciesTextures(model, species);

        var anim = model.Behaviors.OfType<Animator>().FirstOrDefault()
            ?? model.Children.SelectMany(c => c.Behaviors.OfType<Animator>()).FirstOrDefault();
        if (anim != null)
        {
            anim.EnsureBuilt();
            if (anim.States.ContainsKey("Idle")) anim.Play("Idle");
            else if (anim.States.ContainsKey("Walk")) anim.Play("Walk");
        }

        var animal = new HerdAnimal
        {
            Go = host,
            Model = model,
            Anim = anim,
            Species = species,
            Dir = dir,
            Heading = Hash01(species, _herd.Count + 3) * MathF.Tau,
            Spacing = layer.HerdSpacing,
            Diurnal = layer.Diurnal,
            IdleLeft = 1.5f + Hash01(species, 4) * 3f
        };
        Seat(terrain, animal, SN.Vector3.Zero);
        _herd.Add(animal);
        return true;
    }

    void StepAnimal(PlanetTerrain terrain, HerdAnimal animal, float dt)
    {
        float radius = Math.Max(1f, terrain.SampleStandWorldRadius(animal.Dir));
        animal.IdleLeft -= dt;
        bool walking = animal.IdleLeft <= 0f;
        if (animal.IdleLeft < -4.5f)
            animal.IdleLeft = 2f + Hash01(animal.Species, (int)(animal.Heading * 8f) & 255) * 4f;

        SN.Vector3 travel = SN.Vector3.Zero;
        if (walking)
        {
            var tangent = Tangent(animal.Dir, animal.Heading);
            float step = 2.6f * dt / radius;
            var next = SN.Vector3.Normalize(animal.Dir + tangent * step);
            // Flat land was classified as ocean, so every step added a big turn and they spun.
            // Only leave a step that is actually under the sea. Same stand radius the player uses.
            float nextR = terrain.SampleStandWorldRadius(next);
            float sea = terrain.Config != null ? terrain.Config.SeaLevel : 0f;
            bool underwater = terrain.WorldToLocalLength(nextR) < sea - 0.05f;
            if (underwater)
                animal.Heading += dt * 1.2f;
            else
            {
                animal.Dir = next;
                animal.Heading += dt * 0.12f;
                travel = tangent;
            }
        }

        Seat(terrain, animal, travel);
        PlayLocomotion(animal, walking);
    }

    void Seat(PlanetTerrain terrain, HerdAnimal animal, SN.Vector3 travel)
    {
        if (animal.Go == null) return;
        var center = terrain.GetWorldCenter();
        float r = terrain.SampleStandWorldRadius(animal.Dir);
        // Radial up plants the chest and leaves the downhill feet in the air.
        // Tilt onto the slope so the whole body stays on the hill.
        // Four shell samples per animal per frame was a visible hitch once a herd was up.
        SN.Vector3 up;
        bool reuseNormal = animal.HasNormal
            && SN.Vector3.Dot(animal.Dir, animal.NormalDir) > 0.9999f;
        if (reuseNormal)
            up = animal.Normal;
        else
        {
            up = GroundNormal(terrain, animal.Dir, r);
            animal.Normal = up;
            animal.NormalDir = animal.Dir;
            animal.HasNormal = true;
        }
        var pos = center + animal.Dir * r;
        SceneGraphUtil.SetPositionWorld(animal.Go, pos);
        var fwd = travel.LengthSquared() > 1e-6f ? travel : Tangent(animal.Dir, animal.Heading);
        if (fwd.LengthSquared() > 1e-8f)
            animal.Facing = fwd;
        // Pack meshes face +Z. AlignLocalUp aims local -Z along the hint.
        var face = animal.Facing.LengthSquared() > 1e-8f ? animal.Facing : fwd;
        TransformUtil.AlignLocalUp(animal.Go.Transform, up, -face);
    }

    static void PlayLocomotion(HerdAnimal animal, bool walking)
    {
        var anim = animal.Anim;
        if (anim == null) return;
        string want = walking ? "Walk" : "Idle";
        if (!anim.States.ContainsKey(want))
            want = walking ? "Run" : "Idle";
        if (!anim.States.ContainsKey(want)) return;
        // Die and Eat are in the file list. They must not keep playing.
        if (string.Equals(animal.Pose, want, StringComparison.OrdinalIgnoreCase)) return;
        animal.Pose = want;
        anim.Play(want, 0.2f);
    }

    bool AllowsAnyLayer(string species, BiomeDefinition biome)
    {
        for (int i = 0; i < _layers.Length; i++)
        {
            var layer = _layers[i];
            if (layer == null) continue;
            if (!string.Equals(layer.SpeciesId, species, StringComparison.OrdinalIgnoreCase)) continue;
            if (AllowsBiome(layer, biome)) return true;
        }
        return false;
    }

    static bool AllowsBiome(FaunaLayerRecipe layer, BiomeDefinition biome)
    {
        string name = biome.Name ?? "";
        if (name.Length == 0) return false;
        // Test pass: deer show on every land biome. The graph mask comes back later.
        if (string.Equals(layer.SpeciesId, "Deer", StringComparison.OrdinalIgnoreCase))
            return !IsWaterBiome(biome);
        bool listed = false;
        if (NameHit(name, layer.TargetBiome)) listed = true;
        if (!listed && !string.IsNullOrWhiteSpace(layer.BiomeMask))
        {
            var parts = layer.BiomeMask.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < parts.Length; i++)
                if (NameHit(name, parts[i])) { listed = true; break; }
        }
        if (!listed && string.IsNullOrWhiteSpace(layer.TargetBiome) && string.IsNullOrWhiteSpace(layer.BiomeMask))
            listed = !biome.SpawnWater;
        if (!listed) return false;
        if (biome.SpawnWater && !NameHit(name, layer.TargetBiome) && !MaskHas(layer.BiomeMask, name))
            return false;
        return true;
    }

    static bool MaskHas(string? mask, string biomeName)
    {
        if (string.IsNullOrWhiteSpace(mask)) return false;
        var parts = mask.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
            if (NameHit(biomeName, parts[i])) return true;
        return false;
    }

    static bool IsWaterBiome(BiomeDefinition biome)
    {
        var n = biome.Name ?? "";
        // "water" as a substring also matches Wetlands. Only real water bodies.
        if (n.Contains("ocean", StringComparison.OrdinalIgnoreCase)
            || n.Contains("sea", StringComparison.OrdinalIgnoreCase)
            || n.Contains("lake", StringComparison.OrdinalIgnoreCase)
            || n.Contains("river", StringComparison.OrdinalIgnoreCase))
            return true;
        return biome.SpawnWater && n.Contains("water", StringComparison.OrdinalIgnoreCase);
    }

    static bool NameHit(string biome, string? token)
    {
        if (string.IsNullOrWhiteSpace(biome) || string.IsNullOrWhiteSpace(token)) return false;
        biome = biome.Trim();
        token = token.Trim();
        return biome.Contains(token, StringComparison.OrdinalIgnoreCase)
            || token.Contains(biome, StringComparison.OrdinalIgnoreCase);
    }

    bool TooClose(string species, SN.Vector3 pos, float spacing)
    {
        float min = spacing * spacing;
        for (int i = 0; i < _herd.Count; i++)
        {
            var other = _herd[i];
            if (other.Go == null) continue;
            if (!string.Equals(other.Species, species, StringComparison.OrdinalIgnoreCase)) continue;
            var p = SceneGraphUtil.AccumulateWorld(other.Go).Translation;
            if (SN.Vector3.DistanceSquared(p, pos) < min) return true;
        }
        return false;
    }

    SpeciesAssets? ResolveSpecies(string species)
    {
        if (_species.TryGetValue(species, out var cached)) return cached;
        if (_missing.Contains(species)) return null;
        var assetsRoot = ProjectService.Current?.AssetsPath;
        if (string.IsNullOrWhiteSpace(assetsRoot))
        {
            LogIdle("project assets path is not set");
            return null;
        }
        string? model = FindFile(Path.Combine(assetsRoot, "Animals", "Models"), species, "_Rig", ".fbx")
            ?? FindFile(Path.Combine(assetsRoot, "Animals", "Models"), species, "", ".fbx");
        if (model == null)
        {
            _missing.Add(species);
            Log.Warning($"[Fauna] No model for species '{species}' under Assets/Animals/Models.");
            return null;
        }
        string animDir = Path.Combine(assetsRoot, "Animals", "Animations");
        var assets = new SpeciesAssets
        {
            ModelPath = model,
            IdlePath = FindFile(animDir, species, "_Idle", ".fbx"),
            WalkPath = FindFile(animDir, species, "_Walk", ".fbx"),
            RunPath = FindFile(animDir, species, "_Run", ".fbx")
        };
        _species[species] = assets;
        return assets;
    }

    static string? FindFile(string dir, string species, string suffix, string ext)
    {
        if (!Directory.Exists(dir)) return null;
        string exact = Path.Combine(dir, species + suffix + ext);
        if (File.Exists(exact)) return exact;
        exact = Path.Combine(dir, species + suffix + ext.ToUpperInvariant());
        if (File.Exists(exact)) return exact;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Contains(species, StringComparison.OrdinalIgnoreCase)
                && (suffix.Length == 0 || name.Contains(suffix.TrimStart('_'), StringComparison.OrdinalIgnoreCase))
                && file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return file;
        }
        return null;
    }

    static void ApplySpeciesTextures(GameObject model, string species)
    {
        var proj = ProjectService.Current;
        if (proj == null || string.IsNullOrWhiteSpace(proj.AssetsPath)) return;
        string texDir = Path.Combine(proj.AssetsPath, "Animals", "Textures");
        string? albedo = FindSpeciesMap(texDir, species, "col", "albedo", "diffuse");
        if (albedo == null) return;
        string? normal = FindSpeciesMap(texDir, species, "nrm", "normal");
        string? ao = FindSpeciesMap(texDir, species, "ao", "occl");

        void Walk(GameObject go)
        {
            foreach (var mr in go.Behaviors.OfType<MeshRenderer>())
            {
                var mat = mr.Material ?? new Material();
                if (!HasMap(mat, "albedo") && !HasMap(mat, "diffuse"))
                {
                    AddMap(mat, albedo, "Albedo", proj.RootPath);
                    if (normal != null && !HasMap(mat, "normal"))
                        AddMap(mat, normal, "Normal", proj.RootPath);
                    if (ao != null && !HasMap(mat, "ambient") && !HasMap(mat, "occl"))
                        AddMap(mat, ao, "AmbientOcclusion", proj.RootPath);
                    mat.BaseColor = Avalonia.Media.Color.FromArgb(255, 255, 255, 255);
                }
                mr.Material = mat;
                // The saved .material often has only the normal. Reloading it
                // replaces this color map and the deer draws white.
                mr.MaterialPaths.Clear();
            }
            foreach (var child in go.Children)
                Walk(child);
        }
        Walk(model);
    }

    static bool HasMap(Material mat, string token)
    {
        foreach (var slot in mat.Textures)
        {
            if (slot is not RuntimeTexSlot rts) continue;
            var usage = rts.Usage ?? "";
            if (usage.Contains(token, StringComparison.OrdinalIgnoreCase) && rts.Texture != null)
                return true;
        }
        return false;
    }

    static void AddMap(Material mat, string absPath, string usage, string? projectRoot)
    {
        var tex = Texture2D.FromFile(absPath);
        string rel = absPath;
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            try
            {
                rel = Path.GetRelativePath(projectRoot, absPath);
                if (rel.StartsWith("..", StringComparison.Ordinal)) rel = absPath;
            }
            catch { rel = absPath; }
        }
        mat.Textures.Add(new RuntimeTexSlot
        {
            Usage = usage,
            Texture = tex,
            SourcePath = rel.Replace('\\', '/'),
            FaceMask = -1
        });
    }

    static string? FindSpeciesMap(string dir, string species, params string[] tokens)
    {
        if (!Directory.Exists(dir)) return null;
        string? best = null;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.Contains(species, StringComparison.OrdinalIgnoreCase)) continue;
            bool hit = false;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (name.Contains(tokens[i], StringComparison.OrdinalIgnoreCase))
                {
                    hit = true;
                    break;
                }
            }
            if (!hit) continue;
            if (best == null || name.Contains("col6", StringComparison.OrdinalIgnoreCase))
                best = file;
        }
        return best;
    }

    static float BodyScale(string species)
    {
        var s = species.ToLowerInvariant();
        if (s.Contains("rabbit") || s.Contains("rat") || s.Contains("frog") || s.Contains("chicken")
            || s.Contains("snail") || s.Contains("butterfly") || s.Contains("scorpion"))
            return 1.15f;
        if (s.Contains("deer"))
            return 5f;
        if (s.Contains("bear") || s.Contains("cattle") || s.Contains("croc") || s.Contains("shark")
            || s.Contains("boar") || s.Contains("wolf"))
            return 2.6f;
        return 2.1f;
    }

    /// <summary>
    /// Remove rig branches with nothing to draw. Skinning reads the Skeleton, not these
    /// nodes, and every FBX joint imports as 3-4 pivot objects that the renderer, the
    /// shadow pass and each behavior tick walked every frame for every animal.
    /// </summary>
    static void PruneEmptyRigNodes(GameObject root)
    {
        bool KeepBranch(GameObject go)
        {
            bool keep = go.Behaviors.Count > 0;
            for (int i = go.Children.Count - 1; i >= 0; i--)
            {
                var child = go.Children[i];
                if (child == null) continue;
                if (KeepBranch(child))
                    keep = true;
                else
                    child.RemoveFromParent();
            }
            return keep;
        }
        KeepBranch(root);
    }

    /// <summary>
    /// Drop the model so its lowest vertex sits on the host origin. The rig pivot
    /// is in the body, which left the deer floating or sunk depending on the slope.
    /// </summary>
    static void PlantFeet(GameObject model)
    {
        float minY = float.PositiveInfinity;
        ScanFeet(model, SN.Matrix4x4.Identity, ref minY);
        if (float.IsPositiveInfinity(minY) || MathF.Abs(minY) < 1e-4f)
            return;
        var p = model.Transform.Position;
        model.Transform.Position = new Vector3(p.X, p.Y - minY, p.Z);
    }

    static void ScanFeet(GameObject go, SN.Matrix4x4 parentToHost, ref float minY)
    {
        var toHost = TransformUtil.WorldFromTransform(go.Transform) * parentToHost;
        var mesh = go.Behaviors?.OfType<MeshFilter>().FirstOrDefault()?.Mesh;
        if (mesh != null)
        {
            mesh.GetLocalBounds(out var bmin, out var bmax);
            for (int i = 0; i < 8; i++)
            {
                var corner = new SN.Vector3(
                    (i & 1) == 0 ? bmin.X : bmax.X,
                    (i & 2) == 0 ? bmin.Y : bmax.Y,
                    (i & 4) == 0 ? bmin.Z : bmax.Z);
                float y = corner.X * toHost.M12 + corner.Y * toHost.M22 + corner.Z * toHost.M32 + toHost.M42;
                if (y < minY) minY = y;
            }
        }
        if (go.Children == null) return;
        for (int i = 0; i < go.Children.Count; i++)
        {
            var child = go.Children[i];
            if (child != null) ScanFeet(child, toHost, ref minY);
        }
    }

    /// <summary>Outward normal of the stand surface, so fauna lie on a hill instead of the planet radius.</summary>
    static SN.Vector3 GroundNormal(PlanetTerrain terrain, SN.Vector3 dir, float radius)
    {
        if (dir.LengthSquared() < 1e-8f) return SN.Vector3.UnitY;
        dir = SN.Vector3.Normalize(dir);
        float ang = 2.4f / MathF.Max(radius, 1f);
        var axis = MathF.Abs(dir.Y) < 0.92f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
        var east = SN.Vector3.Normalize(SN.Vector3.Cross(dir, axis));
        var north = SN.Vector3.Cross(east, dir);
        SN.Vector3 At(SN.Vector3 tangent)
        {
            var d = SN.Vector3.Normalize(dir + tangent * ang);
            return d * terrain.SampleStandWorldRadius(d);
        }
        var n = SN.Vector3.Cross(At(east) - At(-east), At(north) - At(-north));
        if (n.LengthSquared() < 1e-8f) return dir;
        n = SN.Vector3.Normalize(n);
        if (SN.Vector3.Dot(n, dir) < 0f) n = -n;
        return n;
    }

    static SN.Vector3 Tangent(SN.Vector3 up, float heading)
    {
        var axis = MathF.Abs(up.Y) < 0.92f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
        var east = SN.Vector3.Normalize(SN.Vector3.Cross(up, axis));
        var north = SN.Vector3.Cross(east, up);
        return north * MathF.Cos(heading) + east * MathF.Sin(heading);
    }

    static float Hash01(string salt, int n)
    {
        unchecked
        {
            int h = 17;
            for (int i = 0; i < salt.Length; i++)
                h = h * 31 + salt[i];
            h = h * 31 + n * 73856093;
            h &= 0x7fffffff;
            return (h % 10000) / 10000f;
        }
    }

    void EnsureRoot()
    {
        if (_root != null && _root.Parent != null) return;
        _root = new GameObject("PlanetFauna");
        if (gameObject != null) gameObject.AddChild(_root);
        else SceneService.Add(_root);
    }

    void ClearHerd()
    {
        for (int i = 0; i < _herd.Count; i++)
        {
            var go = _herd[i].Go;
            if (go == null) continue;
            try { SceneService.Destroy(go); }
            catch { try { go.RemoveFromParent(); } catch { } }
        }
        _herd.Clear();
        if (_root != null)
        {
            try { SceneService.Destroy(_root); }
            catch { try { _root.RemoveFromParent(); } catch { } }
            _root = null;
        }
    }

    sealed class HerdAnimal
    {
        public GameObject? Go;
        public GameObject? Model;
        public Animator? Anim;
        public string Species = "";
        public SN.Vector3 Dir;
        public SN.Vector3 Facing;
        public SN.Vector3 Normal;
        public SN.Vector3 NormalDir;
        public bool HasNormal;
        public float Heading;
        public float Spacing;
        public float IdleLeft;
        public bool Diurnal;
        public string Pose = "";
    }

    sealed class SpeciesAssets
    {
        public string ModelPath = "";
        public string? IdlePath;
        public string? WalkPath;
        public string? RunPath;
    }
}
