#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game_Engine.Core.Biome;
using Game_Engine.Core.Biome.Graph;
using Game_Engine.Core.Importers;
using Game_Engine.Core.Planet;
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
    LayerInfo[] _layerInfo = Array.Empty<LayerInfo>();
    readonly List<HerdAnimal> _herd = new();
    readonly Dictionary<string, SpeciesAssets> _species = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, RigTemplate> s_templates = new(StringComparer.OrdinalIgnoreCase);
    static readonly System.Collections.Concurrent.ConcurrentQueue<RigTemplate> s_pendingImports = new();
    static int s_importerRunning;
    readonly List<MeshFilter> _filterScratch = new();
    readonly List<MeshRenderer> _rendererScratch = new();
    readonly Dictionary<string, string[]> _dirFiles = new(StringComparer.OrdinalIgnoreCase);
    GameObject? _root;
    float _spawnWait;
    bool _loggedIdle;
    PlanetTerrain? _terrain;
    PlanetLifeStreaming? _life;
    RigidbodyPlayer? _player;
    float _playerSearchWait;
    /// <summary>Layer the next spawn pass starts on, so one layer with no room does not hold up the rest.</summary>
    int _nextLayer;

    /// <summary>Spot searches run per spawn pass. Each is up to 16 biome and water samples.</summary>
    const int MaxSpotSearchesPerPass = 4;
    const float FailedSearchRetrySec = 3f;
    const float FailedSearchMoveM = 15f;

    public FaunaLayerRecipe[] Layers => _layers;

    public void Bind(FaunaLayerRecipe[]? layers)
    {
        _layers = layers ?? Array.Empty<FaunaLayerRecipe>();
        _layerInfo = new LayerInfo[_layers.Length];
        for (int i = 0; i < _layers.Length; i++)
        {
            var layer = _layers[i];
            string species = (layer?.SpeciesId ?? "").Trim();
            _layerInfo[i] = new LayerInfo
            {
                Species = species,
                Aquatic = IsAquaticSpecies(species),
                Mask = string.IsNullOrWhiteSpace(layer?.BiomeMask)
                    ? Array.Empty<string>()
                    : layer!.BiomeMask.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            };
        }
        _nextLayer = 0;
        // A new bind looks at the asset folders afresh.
        _dirFiles.Clear();
        ClearHerd();
#if XBOX_UWP
        // Assimp FBX imports during the first planet frames reboot Dev Mode. Wait for biomes/veg to settle.
        _spawnWait = 18f;
#else
        _spawnWait = 0f;
#endif
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
        {
            _terrain = GetComponent<PlanetTerrain>();
            _life = GetComponent<PlanetLifeStreaming>();
        }
        var terrain = _terrain;
        if (terrain == null || terrain.Config == null) return;

        float dt = Math.Max(0f, Time.deltaTime);
#if XBOX_UWP
        // Assimp + GPU uploads while biome maps are still landing reboots Dev Mode.
        if (SceneRenderer.BiomeTexturesStreaming)
            return;
#endif
        SN.Vector3 center = terrain.GetWorldCenter();
        bool haveCam = TryPlayerAnchor(terrain, center, out var anchor);
        SN.Vector3 camDir = haveCam ? SN.Vector3.Normalize(anchor - center) : SN.Vector3.UnitY;
        // The camera's cell is the same for the whole herd this frame.
        int edge = StreamingCellsPerEdge();
        var camCell = haveCam ? CellFor(camDir, edge) : default;
        for (int i = _herd.Count - 1; i >= 0; i--)
        {
            var animal = _herd[i];
            if (animal.Go == null || animal.Go.Parent == null)
            {
                _herd.RemoveAt(i);
                continue;
            }
            if (haveCam && !CellInCameraRing(animal, camCell, camDir))
            {
                animal.Go.RemoveFromParent();
                _herd.RemoveAt(i);
                continue;
            }
            if (!animal.Go.Enabled)
                animal.Go.Enabled = true;
            StepAnimal(terrain, animal, dt);
            // Only a walking animal changes cell.
            if (animal.Cell.Edge != edge || animal.Dir != animal.CellDir)
            {
                animal.Cell = CellFor(animal.Dir, edge);
                animal.CellDir = animal.Dir;
            }
        }

        _playerSearchWait -= dt;
        _spawnWait -= dt;
        if (_spawnWait > 0f) return;
#if XBOX_UWP
        _spawnWait = 1.75f;
#else
        _spawnWait = 0.35f;
#endif
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
        float failMoveSq = 0f;
        float now = Time.time;
        int searches = 0;
        int count = _layers.Length;
        if (_layerInfo.Length != count) return;
        for (int step = 0; step < count; step++)
        {
            int li = (_nextLayer + step) % count;
            var layer = _layers[li];
            if (layer == null || layer.Density <= 0.001f) continue;
            var info = _layerInfo[li];
            string species = info.Species;
            if (species.Length == 0) continue;
            any = true;
            if (_missing.Contains(species)) continue;

            float fish = info.Aquatic ? AquaticFishScale(terrain) : 1f;
            if (fish <= 0.001f) continue;
#if XBOX_UWP
            int want = Math.Clamp((int)MathF.Round(Math.Max(layer.Density, 0.2f) * 12f * fish), 0, 2);
#else
            int want = Math.Clamp((int)MathF.Round(Math.Max(layer.Density, 0.2f) * 30f * fish), 0, 6);
#endif
            if (want < 1) continue;
            int have = 0;
            for (int i = 0; i < _herd.Count; i++)
                if (string.Equals(_herd[i].Species, species, StringComparison.OrdinalIgnoreCase))
                    have++;
            if (have >= want) continue;
            var assets = ResolveSpecies(species);
            if (assets == null) continue;

            // The rig imports on a worker. Until it lands there is nothing to place.
            var rig = PeekTemplate(assets.ModelPath);
            if (rig != null && rig.Failed)
            {
                Log.Warning($"[Fauna] {species} import failed: {rig.Error}");
                _missing.Add(species);
                // Not kept as failed for good: the next play session gets a fresh try.
                lock (s_templates)
                    s_templates.Remove(assets.ModelPath);
                continue;
            }
            if (rig != null && !rig.Loaded) continue;

            // A landed rig still has to be turned into something animals are copied from.
            // That is two steps of game-thread work, taken one per pass so neither shares
            // a frame with a spawn.
            if (rig != null && !rig.Ready)
            {
                PrepareRig(rig, assets, species);
                _nextLayer = li;
                return;
            }

            if (!anchored)
            {
                center = terrain.GetWorldCenter();
                if (!TryPlayerAnchor(terrain, center, out var anchor))
                {
                    LogIdle("no player position yet");
                    return;
                }
                camDir = SN.Vector3.Normalize(anchor - center);
                float worldR = Math.Max(1f, (anchor - center).Length());
                failMoveSq = (FailedSearchMoveM / worldR) * (FailedSearchMoveM / worldR);
                anchored = true;
            }

            // The candidate spots come from the species, the herd size and where the player
            // stands. Until one of those changes, a search that found no ground finds none again.
            // (A wait longer than the retry window means the play clock restarted.)
            if (now < info.RetryAt
                && info.RetryAt - now <= FailedSearchRetrySec
                && info.FailedHerd == _herd.Count
                && (camDir - info.FailedDir).LengthSquared() < failMoveSq)
                continue;
            if (searches >= MaxSpotSearchesPerPass)
            {
                _nextLayer = li;
                return;
            }
            searches++;

            float spacing = layer.HerdSpacing > 0.5f ? layer.HerdSpacing : 18f;
            if (!TryPickSpot(terrain, camDir, center, info, spacing, layer, out var dir, out var pos, out var biomeName))
            {
                info.RetryAt = now + FailedSearchRetrySec;
                info.FailedHerd = _herd.Count;
                info.FailedDir = camDir;
                continue;
            }

            // Import only species that have somewhere to stand near the player.
            if (rig == null)
            {
                RequestTemplate(assets, species);
                continue;
            }

            if (!TrySpawn(terrain, layer, info, assets, rig, dir, pos))
                continue;
            Log.Info($"[Fauna] {species} on {biomeName}.");
            _nextLayer = (li + 1) % count;
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

    bool TryPickSpot(PlanetTerrain terrain, SN.Vector3 camDir, SN.Vector3 center, LayerInfo info,
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

        string species = info.Species;
        string last = "";
        bool wantNote = !_loggedIdle && _herd.Count == 0;
        bool aquatic = info.Aquatic;
        int edge = StreamingCellsPerEdge();
        for (int attempt = 0; attempt < 16; attempt++)
        {
            float ang = (Hash01(species, _herd.Count * 17 + attempt * 13) * 2f - 1f) * MathF.PI;
            float reach = aquatic ? 80f : 36f;
            float dist = attempt == 0 ? (aquatic ? 18f : 8f) : Math.Clamp(4f + Hash01(species, 90 + attempt) * reach, 4f, reach);
            var tangent = Tangent(camDir, ang);
            dir = SN.Vector3.Normalize(camDir + tangent * (dist / stand));
            if (!TryReadSurface(terrain, dir, wantNote, aquatic, out var biome, out _, out last))
                continue;
            biomeName = biome?.Name ?? "land";
            if (biome != null && !AllowsBiome(layer, info.Mask, biome))
                continue;
            if (biome == null)
                continue;
            // Biome name "Ocean" still covers dry shore. Swimmers need real water.
            if (aquatic)
            {
                if (!IsSubmerged(terrain, dir, 1.2f)) continue;
            }
            else if (IsSubmerged(terrain, dir, 0.35f))
                continue;
            float ground = terrain.SampleStandWorldRadius(dir);
            if (ground < 1f) continue;
            pos = center + dir * ground;
            if (TooClose(species, pos, Math.Min(spacing, 12f))) continue;
            var cell = CellFor(dir, edge);
            if (cell.IsSet && SpeciesOccupiesCell(species, cell)) continue;
            return true;
        }
        LogIdle($"no {(aquatic ? "water" : "land")} spot for {species} ({last})");
        return false;
    }

    /// <summary>
    /// Same land test the vegetation spawner uses: heightfield vs sea, biome from
    /// normalized altitude. The world-position blend crushed near-shore land into Ocean.
    /// </summary>
    static bool TryReadSurface(PlanetTerrain terrain, SN.Vector3 dir, bool wantNote, bool allowUnderwater, out BiomeDefinition? biome, out float worldR, out string note)
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
        if (!allowUnderwater && localR < sea + 0.15f)
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

    bool TrySpawn(PlanetTerrain terrain, FaunaLayerRecipe layer, LayerInfo info, SpeciesAssets assets,
        RigTemplate rig, SN.Vector3 dir, SN.Vector3 pos)
    {
        string species = info.Species;
        GameObject model;
        try
        {
            model = NewAnimal(rig, assets.ModelPath);
        }
        catch (Exception ex)
        {
            Log.Warning($"[Fauna] {species} import failed: {ex.Message}");
            _missing.Add(species);
            return false;
        }

        var host = new GameObject(species);
        EnsureRoot();
        _root!.AddChild(host);
        host.AddChild(model);
        float scale = layer.Scale > 0f ? layer.Scale : 1f;
        model.Transform.Scale = new Vector3(scale, scale, scale);
        model.Transform.Position = new Vector3(0, 0, 0);
        PlantFeet(model);
        try
        {
            ApplySpeciesTextures(model, assets);
        }
        catch (Exception ex)
        {
            // A bad map used to escape after the animal was already in the scene, so the
            // next frame imported it again. That pile is what froze the load.
            Log.Warning($"[Fauna] {species} textures failed: {ex.Message}");
            host.RemoveFromParent();
            _missing.Add(species);
            return false;
        }

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
            Aquatic = info.Aquatic,
            Dir = dir,
            Heading = Hash01(species, _herd.Count + 3) * MathF.Tau,
            Spacing = layer.HerdSpacing,
            Diurnal = layer.Diurnal,
            IdleLeft = 1.5f + Hash01(species, 4) * 3f,
            Cell = CellFor(dir, StreamingCellsPerEdge()),
            CellDir = dir
        };
        Seat(terrain, animal, SN.Vector3.Zero);
        _herd.Add(animal);
        return true;
    }

    void StepAnimal(PlanetTerrain terrain, HerdAnimal animal, float dt)
    {
        bool aquatic = animal.Aquatic;
        if (aquatic && !IsSubmerged(terrain, animal.Dir, 0.6f))
        {
            animal.Go!.RemoveFromParent();
            return;
        }
        animal.IdleLeft -= dt;
        bool walking = animal.IdleLeft <= 0f;
        if (animal.IdleLeft < -4.5f)
            animal.IdleLeft = 2f + Hash01(animal.Species, (int)(animal.Heading * 8f) & 255) * 4f;

        SN.Vector3 travel = SN.Vector3.Zero;
        if (walking)
        {
            float radius = Math.Max(1f, terrain.SampleStandWorldRadius(animal.Dir));
            var tangent = Tangent(animal.Dir, animal.Heading);
            float step = 2.6f * dt / radius;
            var next = SN.Vector3.Normalize(animal.Dir + tangent * step);
            // Land herds turn back at the shoreline. Swimmers turn back onto land.
            bool nextWet = IsSubmerged(terrain, next, aquatic ? 0.8f : 0.35f);
            bool blocked = aquatic ? !nextWet : nextWet;
            if (blocked)
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
        animal.WorldPos = pos;
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

    /// <summary>Same rule as the overload above, with the layer's biome mask already split.</summary>
    static bool AllowsBiome(FaunaLayerRecipe layer, string[] mask, BiomeDefinition biome)
    {
        string name = biome.Name ?? "";
        if (name.Length == 0) return false;
        bool target = NameHit(name, layer.TargetBiome);
        bool inMask = false;
        for (int i = 0; i < mask.Length; i++)
            if (NameHit(name, mask[i])) { inMask = true; break; }
        bool listed = target || inMask;
        if (!listed && string.IsNullOrWhiteSpace(layer.TargetBiome) && string.IsNullOrWhiteSpace(layer.BiomeMask))
            listed = !biome.SpawnWater;
        if (!listed) return false;
        if (biome.SpawnWater && !target && !inMask)
            return false;
        return true;
    }

    PlanetLifeStreaming? LifeStreaming()
    {
        // Added when the graph is applied, which can be after this component.
        if (_life == null || _life.gameObject != gameObject)
            _life = GetComponent<PlanetLifeStreaming>();
        return _life;
    }

    /// <summary>
    /// Aquatic herd size multiplier from underwater recipes. 1 keeps the fauna-layer count.
    /// 0 skips swimmers. No underwater recipe leaves the fauna layer unchanged.
    /// </summary>
    float AquaticFishScale(PlanetTerrain terrain)
    {
        var layers = LifeStreaming()?.UnderwaterLife;
        if (layers == null || layers.Length == 0) return 1f;
        float best = 0f;
        bool any = false;
        bool water = terrain.EnableWater;
        for (int i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            if (layer == null) continue;
            if (layer.RequireWaterPlanet && !water) continue;
            any = true;
            if (layer.FishDensity > best) best = layer.FishDensity;
        }
        return any ? Math.Max(0f, best) : 1f;
    }

    int StreamingCellsPerEdge()
    {
        int edge = LifeStreaming()?.CellsPerEdge ?? PlanetLifeStreaming.CellsPerFaceEdge;
        return Math.Max(1, edge);
    }

    /// <summary>
    /// Cube-face cell of the streaming grid, the same cell <see cref="PlanetLifeStreaming.MakeCellKey(int, float, float, int)"/>
    /// names. Kept as numbers: the herd is checked against the camera cell every frame, and
    /// building and re-parsing a key string per animal per frame was most of that check.
    /// </summary>
    readonly record struct FaunaCell(int Face, int U, int V, int Edge)
    {
        /// <summary>False for a degenerate direction, which has no cell.</summary>
        public bool IsSet => Edge > 0;
    }

    static FaunaCell CellFor(SN.Vector3 dir, int edge)
    {
        if (dir.LengthSquared() < 1e-8f) return default;
        var uv = CubeSphereMath.SphereToCubeExact(SN.Vector3.Normalize(dir));
        var (cu, cv) = PlanetLifeStreaming.CellCoords(uv.U, uv.V, edge);
        return new FaunaCell(uv.Face, cu, cv, Math.Max(1, edge));
    }

    bool SpeciesOccupiesCell(string species, FaunaCell cell)
    {
        for (int i = 0; i < _herd.Count; i++)
        {
            var other = _herd[i];
            if (!string.Equals(other.Species, species, StringComparison.OrdinalIgnoreCase)) continue;
            if (other.Cell == cell) return true;
        }
        return false;
    }

    /// <summary>True when the cell is on the camera face within two cells, or still close on a neighbor face.</summary>
    static bool CellInCameraRing(HerdAnimal animal, FaunaCell camCell, SN.Vector3 camDir)
    {
        var cell = animal.Cell;
        if (!cell.IsSet || !camCell.IsSet) return true;
        // A cell from an older grid size is refreshed after this frame's step.
        if (cell.Edge != camCell.Edge) return true;
        if (cell.Face == camCell.Face)
            return Math.Abs(cell.U - camCell.U) <= 2 && Math.Abs(cell.V - camCell.V) <= 2;
        float d = animal.Dir.LengthSquared() > 1e-8f ? SN.Vector3.Dot(SN.Vector3.Normalize(animal.Dir), camDir) : 0f;
        return d > 0.92f;
    }

    /// <summary>Fish and other swimmers. They stay under the water surface, not on dry shore.</summary>
    static bool IsAquaticSpecies(string species)
    {
        var s = species.ToLowerInvariant();
        return s.Contains("shark") || s.Contains("octopus") || s.Contains("perch")
            || s.Contains("pike") || s.Contains("salmon") || s.Contains("crab")
            || s.Contains("croc");
    }

    /// <summary>True when the crust at <paramref name="dir"/> is at least <paramref name="minDepth"/> under the water surface.</summary>
    static bool IsSubmerged(PlanetTerrain terrain, SN.Vector3 dir, float minDepth)
    {
        if (terrain.Config == null || !terrain.EnableWater) return false;
        // Every animal asks this each frame, and a walking one asks again for the step
        // ahead. The water table is the costly half, so it comes from the per-cell cache
        // and dry ground stops there. The crust stays exact for the direction asked.
        var water = terrain.SampleWaterSurfaceCached(dir);
        if (water.Mask < 0.18f || water.Kind == PlanetWaterKind.Lava) return false;
        float crust = terrain.WorldToLocalLength(terrain.SampleStandWorldRadius(dir));
        return water.Radius - crust >= minDepth;
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
            // Where Seat last put it, which is its world position.
            if (SN.Vector3.DistanceSquared(other.WorldPos, pos) < min) return true;
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
        string texDir = Path.Combine(assetsRoot, "Animals", "Textures");
        var assets = new SpeciesAssets
        {
            ModelPath = model,
            IdlePath = FindFile(animDir, species, "_Idle", ".fbx"),
            WalkPath = FindFile(animDir, species, "_Walk", ".fbx"),
            RunPath = FindFile(animDir, species, "_Run", ".fbx"),
            // Looked up once. Every spawn used to list the texture folder three times.
            AlbedoPath = FindSpeciesMap(texDir, species, "col", "albedo", "diffuse"),
            NormalPath = FindSpeciesMap(texDir, species, "nrm", "normal"),
            AoPath = FindSpeciesMap(texDir, species, "ao", "occl")
        };
        _species[species] = assets;
        return assets;
    }

    /// <summary>
    /// The files of one asset folder, listed once per bind. Resolving a species looks
    /// through the model, clip and texture folders seven times between them, and every
    /// layer resolves on the first spawn pass.
    /// </summary>
    string[] FilesIn(string dir)
    {
        if (_dirFiles.TryGetValue(dir, out var files))
            return files;
        try { files = Directory.Exists(dir) ? Directory.GetFiles(dir) : Array.Empty<string>(); }
        catch { files = Array.Empty<string>(); }
        _dirFiles[dir] = files;
        return files;
    }

    string? FindFile(string dir, string species, string suffix, string ext)
    {
        var files = FilesIn(dir);
        if (files.Length == 0) return null;
        // The exact name first, with either case of the extension.
        string exactName = species + suffix + ext;
        for (int i = 0; i < files.Length; i++)
        {
            if (string.Equals(Path.GetFileName(files[i]), exactName, StringComparison.OrdinalIgnoreCase))
                return files[i];
        }
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Contains(species, StringComparison.OrdinalIgnoreCase)
                && (suffix.Length == 0 || name.Contains(suffix.TrimStart('_'), StringComparison.OrdinalIgnoreCase))
                && file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return file;
        }
        return null;
    }

    static void ApplySpeciesTextures(GameObject model, SpeciesAssets assets)
    {
        var proj = ProjectService.Current;
        if (proj == null || string.IsNullOrWhiteSpace(proj.AssetsPath)) return;
        string? albedo = assets.AlbedoPath;
        if (albedo == null) return;
        string? normal = assets.NormalPath;
        string? ao = assets.AoPath;

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

    static RigTemplate? PeekTemplate(string modelPath)
    {
        lock (s_templates)
            return s_templates.TryGetValue(modelPath, out var rig) ? rig : null;
    }

    /// <summary>Queue the species' rig, clips and maps to load off the game thread.</summary>
    static void RequestTemplate(SpeciesAssets assets, string species)
    {
        RigTemplate rig;
        lock (s_templates)
        {
            if (s_templates.ContainsKey(assets.ModelPath))
                return;
            rig = new RigTemplate
            {
                ModelPath = assets.ModelPath,
                Species = species,
                WarmTextures = new[] { assets.AlbedoPath, assets.NormalPath, assets.AoPath }
            };
            s_templates[assets.ModelPath] = rig;
        }
        s_pendingImports.Enqueue(rig);
        if (System.Threading.Interlocked.CompareExchange(ref s_importerRunning, 1, 0) == 0)
            System.Threading.Tasks.Task.Run(ImportLoop);
    }

    /// <summary>One rig at a time, so a planet's worth of species queues up instead of all parsing at once.</summary>
    static void ImportLoop()
    {
        try
        {
            while (s_pendingImports.TryDequeue(out var rig))
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var imported = ModelImporter.ImportModel(rig.ModelPath);
                    PruneEmptyRigNodes(imported);
                    // First use of a clip or map decodes it. Do that here, not on the first spawn.
                    WarmClips(imported);
                    for (int i = 0; i < rig.WarmTextures.Length; i++)
                    {
                        var path = rig.WarmTextures[i];
                        if (path == null) continue;
                        try { Texture2D.FromFile(path); }
                        catch { /* AddMap reports a bad map when the animal spawns */ }
                    }
                    rig.Imported = imported;
                    Log.Info($"[Fauna] {rig.Species} rig loaded in {clock.ElapsedMilliseconds} ms (background).");
                }
                catch (Exception ex)
                {
                    rig.Error = ex.Message;
                    rig.Failed = true;
                }
            }
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref s_importerRunning, 0);
            // A request can land between the last dequeue and the flag reset.
            if (!s_pendingImports.IsEmpty
                && System.Threading.Interlocked.CompareExchange(ref s_importerRunning, 1, 0) == 0)
                System.Threading.Tasks.Task.Run(ImportLoop);
        }
    }

    static void WarmClips(GameObject node)
    {
        foreach (var b in node.Behaviors)
        {
            if (b is not Animator anim) continue;
            foreach (var state in anim.StateList)
            {
                if (!string.IsNullOrWhiteSpace(state.BoneClipPath))
                    BoneAnimationClipAsset.Load(state.BoneClipPath);
            }
        }
        foreach (var child in node.Children)
            WarmClips(child);
    }

    static void CollectMeshFilters(GameObject node, List<MeshFilter> filters)
    {
        foreach (var b in node.Behaviors)
        {
            if (b is MeshFilter mf)
                filters.Add(mf);
        }
        foreach (var child in node.Children)
            CollectMeshFilters(child, filters);
    }

    /// <summary>
    /// One step of making a landed rig ready: first the JSON, then the animal that is
    /// restored from it once and copied from after that.
    /// </summary>
    void PrepareRig(RigTemplate rig, SpeciesAssets assets, string species)
    {
        if (rig.Json.Length == 0)
        {
            try
            {
                FinishTemplate(rig);
            }
            catch (Exception ex)
            {
                Log.Warning($"[Fauna] {species} rig could not be saved for reuse: {ex.Message}");
            }
            // With no JSON every animal of this species is imported on its own, as before.
            if (rig.Json.Length == 0)
                rig.NoPrototype = true;
            return;
        }

        try
        {
            var prototype = RestoreRig(rig);
            if (prototype == null)
            {
                rig.NoPrototype = true;
                return;
            }
            // The maps are the same for every animal of the species. They go on once here.
            ApplySpeciesTextures(prototype, assets);
            rig.Prototype = prototype;
        }
        catch (Exception ex)
        {
            Log.Warning($"[Fauna] {species} textures failed: {ex.Message}");
            _missing.Add(species);
            lock (s_templates)
                s_templates.Remove(assets.ModelPath);
        }
    }

    /// <summary>
    /// Turn the worker's import into the JSON the rig is restored from. Two things are
    /// kept aside and handed back after each restore instead of going through the JSON.
    /// The mesh and model path: with a model path in the JSON the scene loader imports
    /// the FBX again for each mesh part. And each renderer's material with its list of
    /// material files: the importer's material carries the maps the FBX names, which the
    /// saved file often lacks, and writing a material out searches the asset folders for
    /// a file of its name.
    /// </summary>
    void FinishTemplate(RigTemplate rig)
    {
        var root = rig.Imported;
        if (root == null) return;
        _filterScratch.Clear();
        CollectMeshFilters(root, _filterScratch);
        int n = _filterScratch.Count;
        rig.Meshes = new Mesh?[n];
        rig.ModelPaths = new string[n];
        rig.PartIndices = new string[n];
        for (int i = 0; i < n; i++)
        {
            var mf = _filterScratch[i];
            rig.Meshes[i] = mf.Mesh;
            rig.ModelPaths[i] = mf.ModelPath;
            rig.PartIndices[i] = mf.ModelPartIndex;
            mf.Mesh = null;
            mf.ModelPath = "";
        }
        _filterScratch.Clear();

        _rendererScratch.Clear();
        CollectRenderers(root, _rendererScratch);
        int r = _rendererScratch.Count;
        rig.Materials = new Material?[r];
        rig.MaterialFiles = new string[r][];
        for (int i = 0; i < r; i++)
        {
            var mr = _rendererScratch[i];
            rig.Materials[i] = mr.Material;
            rig.MaterialFiles[i] = mr.MaterialPaths.ToArray();
            mr.Material = null;
            mr.MaterialPaths.Clear();
        }
        _rendererScratch.Clear();

        rig.Json = SceneSerialization.SerializeGameObjectToJson(root);
        rig.Imported = null;
    }

    static void CollectRenderers(GameObject node, List<MeshRenderer> renderers)
    {
        foreach (var b in node.Behaviors)
        {
            if (b is MeshRenderer mr)
                renderers.Add(mr);
        }
        foreach (var child in node.Children)
            CollectRenderers(child, renderers);
    }

    /// <summary>
    /// The rig as the scene loader restores it, with the rig's meshes and the importer's
    /// materials handed back, so it stands as the import itself did.
    /// </summary>
    GameObject? RestoreRig(RigTemplate rig)
    {
        if (rig.Json.Length == 0)
            return null;
        var restored = SceneSerialization.DeserializeGameObjectFromJson(rig.Json);
        if (restored == null)
            return null;
        _filterScratch.Clear();
        _rendererScratch.Clear();
        CollectMeshFilters(restored, _filterScratch);
        CollectRenderers(restored, _rendererScratch);
        bool lined = _filterScratch.Count == rig.Meshes.Length
            && _rendererScratch.Count == rig.Materials.Length;
        if (lined)
        {
            for (int i = 0; i < _filterScratch.Count; i++)
            {
                var mf = _filterScratch[i];
                mf.Mesh = rig.Meshes[i];
                mf.ModelPath = rig.ModelPaths[i];
                mf.ModelPartIndex = rig.PartIndices[i];
            }
            for (int i = 0; i < _rendererScratch.Count; i++)
            {
                var mr = _rendererScratch[i];
                mr.Material = rig.Materials[i];
                mr.MaterialPaths.Clear();
                mr.MaterialPaths.AddRange(rig.MaterialFiles[i]);
            }
            RebindSkinned(restored);
        }
        _filterScratch.Clear();
        _rendererScratch.Clear();
        return lined ? restored : null;
    }

    /// <summary>A new animal: a copy of the rig's prototype, or the slower routes when there is none.</summary>
    GameObject NewAnimal(RigTemplate rig, string modelPath)
    {
        if (rig.Prototype != null)
        {
            var copy = CopyRig(rig.Prototype);
            if (copy != null)
            {
                RebindSkinned(copy);
                return copy;
            }
            // A component the copy does not know. Restore each animal from JSON instead.
            rig.Prototype = null;
            rig.NoPrototype = true;
        }

        var restored = RestoreRig(rig);
        if (restored != null)
            return restored;

        // The restore did not line up with the rig. Import it the slow way so the animal still appears.
        var imported = ModelImporter.ImportModel(modelPath);
        PruneEmptyRigNodes(imported);
        return imported;
    }

    /// <summary>
    /// Copy of a restored rig for one more animal. A rig is a few objects holding a mesh
    /// filter, a renderer and an animator, so each is copied by hand: the same mesh and
    /// material, and the animator's state table. Nothing is read from disk. Null when the
    /// rig holds a component this does not know how to copy.
    /// </summary>
    static GameObject? CopyRig(GameObject source)
    {
        var copy = new GameObject(source.Name) { Tag = source.Tag, Layer = source.Layer };
        if (!source.Enabled)
            copy.Enabled = false;
        copy.Transform.Position = source.Transform.Position;
        copy.Transform.Rotation = source.Transform.Rotation;
        copy.Transform.Scale = source.Transform.Scale;

        foreach (var b in source.Behaviors)
        {
            Behavior made;
            List<string>? materialPaths = null;
            switch (b)
            {
                case MeshFilter mf:
                {
                    var filter = new MeshFilter
                    {
                        Mesh = mf.Mesh,
                        ModelPath = mf.ModelPath,
                        ModelPartIndex = mf.ModelPartIndex
                    };
                    if (mf.TargetPaths != null)
                        filter.TargetPaths.AddRange(mf.TargetPaths);
                    made = filter;
                    break;
                }
                case MeshRenderer mr when mr.GetType() == typeof(MeshRenderer) || mr.GetType() == typeof(SkinnedMeshRenderer):
                {
                    MeshRenderer renderer = mr is SkinnedMeshRenderer ? new SkinnedMeshRenderer() : new MeshRenderer();
                    renderer.Color = mr.Color;
                    renderer.Wireframe = mr.Wireframe;
                    renderer.LineWidth = mr.LineWidth;
                    renderer.CastShadows = mr.CastShadows;
                    renderer.ReceiveShadows = mr.ReceiveShadows;
                    renderer.DoubleSided = mr.DoubleSided;
                    renderer.InvertFrontFace = mr.InvertFrontFace;
                    renderer.Material = mr.Material;
                    materialPaths = mr.MaterialPaths;
                    made = renderer;
                    break;
                }
                case Animator anim:
                {
                    var animator = new Animator
                    {
                        PlayOnAwake = anim.PlayOnAwake,
                        Speed = anim.Speed,
                        DefaultStateName = anim.DefaultStateName
                    };
                    foreach (var s in anim.StateList)
                    {
                        animator.StateList.Add(new Animator.AnimStateDTO
                        {
                            Name = s.Name,
                            ClipPath = s.ClipPath,
                            BoneClipPath = s.BoneClipPath,
                            Speed = s.Speed,
                            EditorX = s.EditorX,
                            EditorY = s.EditorY
                        });
                    }
                    foreach (var t in anim.TransitionList)
                    {
                        animator.TransitionList.Add(new Animator.AnimTransitionDTO
                        {
                            FromState = t.FromState,
                            ToState = t.ToState,
                            Condition = t.Condition,
                            ConditionValue = t.ConditionValue,
                            BoolCondition = t.BoolCondition,
                            TransitionDuration = t.TransitionDuration,
                            HasExitTime = t.HasExitTime,
                            ExitTime = t.ExitTime
                        });
                    }
                    made = animator;
                    break;
                }
                default:
                    return null;
            }

            made.LogLifecycle = b.LogLifecycle;
            made.SetEnabledSilent(b.Enabled);
            // Same order the scene loader uses: attach, then enable. The renderer is enabled
            // with no material files listed so it keeps the material it was given instead
            // of reading the files again. The list goes on afterwards.
            copy.AddBehaviorForDeserialization(made);
            made.PostDeserialize();
            if (made.Enabled)
                made.OnEnable();
            if (materialPaths != null && made is MeshRenderer listed)
                listed.MaterialPaths.AddRange(materialPaths);
        }

        foreach (var child in source.Children)
        {
            var childCopy = CopyRig(child);
            if (childCopy == null)
                return null;
            copy.AddChild(childCopy);
        }
        return copy;
    }

    /// <summary>The skeleton comes from the mesh, which was not there yet when the JSON was restored.</summary>
    static void RebindSkinned(GameObject node)
    {
        foreach (var b in node.Behaviors)
        {
            if (b is SkinnedMeshRenderer smr && smr.Enabled)
            {
                try { smr.EnsureBoneMatrices(); }
                catch { /* the renderer binds again before it draws */ }
            }
        }
        foreach (var child in node.Children)
            RebindSkinned(child);
    }

    static void AddMap(Material mat, string absPath, string usage, string? projectRoot)
    {
        Texture2D tex;
        try { tex = Texture2D.FromFile(absPath); }
        catch (Exception ex)
        {
            Log.Warning($"[Fauna] Skip {usage} map {Path.GetFileName(absPath)}: {ex.Message}");
            return;
        }
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

    /// <summary>Letters and digits only, so a species id still matches a texture name that has underscores.</summary>
    static string CompactName(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    string? FindSpeciesMap(string dir, string species, params string[] tokens)
    {
        string? best = null;
        foreach (var file in FilesIn(dir))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!CompactName(name).Contains(CompactName(species), StringComparison.Ordinal)) continue;
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
            if (best == null)
                best = file;
        }
        return best;
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
    /// is in the body, which left models floating or sunk depending on the slope.
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
        public bool Aquatic;
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
        public FaunaCell Cell;
        /// <summary>Direction <see cref="Cell"/> was worked out for.</summary>
        public SN.Vector3 CellDir;
        /// <summary>World position from the last seat.</summary>
        public SN.Vector3 WorldPos;
    }

    sealed class SpeciesAssets
    {
        public string ModelPath = "";
        public string? IdlePath;
        public string? WalkPath;
        public string? RunPath;
        public string? AlbedoPath;
        public string? NormalPath;
        public string? AoPath;
    }

    /// <summary>What a fauna layer needs on every spawn pass, worked out once when the table is bound.</summary>
    sealed class LayerInfo
    {
        public string Species = "";
        public bool Aquatic;
        public string[] Mask = Array.Empty<string>();
        /// <summary>A spot search failed. These say when the same search is worth running again.</summary>
        public float RetryAt;
        public int FailedHerd = -1;
        public SN.Vector3 FailedDir;
    }

    /// <summary>
    /// One imported rig that animals are stamped from. The FBX, its clips and its maps
    /// load once on a worker. The rig is then restored from JSON once, the way the scene
    /// loader would, and every animal is an in-memory copy of that. It used to be a full
    /// FBX import per mesh part, per animal, on the game thread.
    /// </summary>
    sealed class RigTemplate
    {
        public string ModelPath = "";
        public string Species = "";
        public string?[] WarmTextures = Array.Empty<string?>();
        /// <summary>Set by the worker when the import is done. Cleared once <see cref="Json"/> is built.</summary>
        public volatile GameObject? Imported;
        public volatile bool Failed;
        public volatile string? Error;
        public string Json = "";
        public Mesh?[] Meshes = Array.Empty<Mesh?>();
        public string[] ModelPaths = Array.Empty<string>();
        public string[] PartIndices = Array.Empty<string>();
        /// <summary>Each renderer's material as the importer made it, and the material files it listed.</summary>
        public Material?[] Materials = Array.Empty<Material?>();
        public string[][] MaterialFiles = Array.Empty<string[]>();
        /// <summary>The restored rig animals are copied from. Never added to the scene.</summary>
        public GameObject? Prototype;
        /// <summary>The rig cannot be copied. Each animal is restored (or imported) on its own.</summary>
        public bool NoPrototype;

        public bool Loaded => Imported != null || Json.Length > 0 || NoPrototype;
        public bool Ready => Prototype != null || NoPrototype;
    }
}
