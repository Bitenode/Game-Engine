#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Game_Engine.Core.Events;
using Game_Engine.Core.Gameplay;
using Game_Engine.Core.Physics;
namespace Game_Engine.Core.Component;

public enum TriggerVolumePreset
{
    Custom,
    DamageZone,
    Checkpoint,
    SceneLoad,
    Activation
}

/// <summary>Designer-facing trigger presets and filters. Requires a sibling <see cref="Collider"/> with <see cref="Collider.IsTrigger"/>.</summary>
[ComponentCategory("Physics")]
public sealed class TriggerVolume : Behavior
{
    [Persist] public TriggerVolumePreset Preset { get; set; } = TriggerVolumePreset.Custom;

    /// <summary>Bits 0–31; if non-zero, only objects whose <see cref="GameObject.Layer"/> is included react.</summary>
    [Persist] public int LayerMask { get; set; }

    /// <summary>If non-empty, only objects with this exact <see cref="GameObject.Tag"/> react.</summary>
    [Persist] public string TagFilter { get; set; } = "";

    [Persist] public bool OneShot { get; set; }
    [Persist] public float CooldownSeconds { get; set; }

    [Persist] public float DamagePerSecond { get; set; } = 10f;
    [Persist] public string PlayerTag { get; set; } = "Player";
    [Persist] public string SceneName { get; set; } = "";
    [Persist] public string? TargetPathOrName { get; set; }
    [Persist] public bool EnableTargetOnEnter { get; set; } = true;
    [Persist] public bool DisableTargetOnExit { get; set; }

    /// <summary>Parallel lists: kind name per row (<see cref="TriggerReactionKind"/>).</summary>
    [Persist] public List<string> OnEnterKinds { get; set; } = new();
    [Persist] public List<string> OnEnterStrings { get; set; } = new();
    [Persist] public List<bool> OnEnterBools { get; set; } = new();

    [Persist] public List<string> OnExitKinds { get; set; } = new();
    [Persist] public List<string> OnExitStrings { get; set; } = new();
    [Persist] public List<bool> OnExitBools { get; set; } = new();

    float _lastFireTime = float.NegativeInfinity;
    bool _consumedOneShot;

    public override void OnTriggerEnter(Collider? other)
    {
        if (!PassesFilters(other)) return;
        if (!PassesCooldown()) return;

        switch (Preset)
        {
            case TriggerVolumePreset.Checkpoint:
                if (other?.gameObject != null && string.Equals(other.gameObject.Tag, PlayerTag, StringComparison.Ordinal))
                    CheckpointService.SetLastCheckpoint(new Vector3(
                        (float)other.gameObject.Transform.Position.X,
                        (float)other.gameObject.Transform.Position.Y,
                        (float)other.gameObject.Transform.Position.Z));
                break;
            case TriggerVolumePreset.SceneLoad:
                if (!string.IsNullOrWhiteSpace(SceneName))
                    SceneManager.LoadScene(SceneName.Trim());
                break;
            case TriggerVolumePreset.Activation:
                ApplyActivation(enter: true);
                break;
        }

        RunReactionLists(OnEnterKinds, OnEnterStrings, OnEnterBools, other);
        MarkFired();
    }

    public override void OnTriggerStay(Collider? other)
    {
        if (Preset != TriggerVolumePreset.DamageZone) return;
        if (!PassesFilters(other)) return;
        if (other?.gameObject == null) return;

        float dt = (float)Time.fixedDeltaTime;
        if (dt <= 0f) dt = (float)Time.deltaTime;
        float dmg = DamagePerSecond * dt;
        if (dmg <= 0f) return;

        foreach (var b in other.gameObject.Behaviors)
        {
            if (b is IDamageable d)
                d.ApplyDamage(dmg);
        }
    }

    public override void OnTriggerExit(Collider? other)
    {
        if (!PassesFilters(other)) return;
        if (Preset == TriggerVolumePreset.Activation && DisableTargetOnExit)
            ApplyActivation(enter: false);
        RunReactionLists(OnExitKinds, OnExitStrings, OnExitBools, other);
    }

    bool PassesFilters(Collider? other)
    {
        if (other?.gameObject == null) return false;
        var go = other.gameObject;
        if (LayerMask != 0 && !LayerMaskUtility.Contains(LayerMask, go.Layer)) return false;
        if (!string.IsNullOrEmpty(TagFilter) && !string.Equals(go.Tag, TagFilter, StringComparison.Ordinal))
            return false;
        return true;
    }

    bool PassesCooldown()
    {
        if (CooldownSeconds <= 0f) return true;
        return Time.time >= _lastFireTime + CooldownSeconds;
    }

    void MarkFired()
    {
        _lastFireTime = Time.time;
        if (OneShot && !_consumedOneShot)
        {
            _consumedOneShot = true;
            Enabled = false;
        }
    }

    void ApplyActivation(bool enter)
    {
        var target = ResolveTarget(TargetPathOrName);
        if (target == null) return;
        if (enter)
            target.Enabled = EnableTargetOnEnter;
        else if (DisableTargetOnExit)
            target.Enabled = false;
    }

    void RunReactionLists(List<string> kinds, List<string> strings, List<bool> bools, Collider? other)
    {
        for (int i = 0; i < kinds.Count; i++)
        {
            var kindStr = kinds[i];
            if (!Enum.TryParse<TriggerReactionKind>(kindStr, ignoreCase: true, out var kind) || kind == TriggerReactionKind.None)
                continue;
            var s = i < strings.Count ? strings[i] : null;
            var b = i < bools.Count && bools[i];
            ExecuteReaction(kind, s, b, other);
        }
    }

    static void ExecuteReaction(TriggerReactionKind kind, string? primary, bool boolVal, Collider? other)
    {
        switch (kind)
        {
            case TriggerReactionKind.LoadScene:
                if (!string.IsNullOrWhiteSpace(primary))
                    SceneManager.LoadScene(primary.Trim());
                break;
            case TriggerReactionKind.SetObjectEnabled:
                var go = ResolveTarget(primary);
                if (go != null) go.Enabled = boolVal;
                break;
            case TriggerReactionKind.PublishChannel:
                if (!string.IsNullOrWhiteSpace(primary))
                    EventBus.Publish(new TriggerVolumeSignal
                    {
                        Channel = primary.Trim(),
                        InstigatorObject = other?.gameObject,
                        InstigatorCollider = other
                    });
                break;
        }
    }

    internal static GameObject? ResolveTarget(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return null;
        var key = pathOrName.Trim().Replace('\\', '/');
        foreach (var root in SceneService.Root)
        {
            if (key.Contains('/'))
            {
                var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) continue;
                var n = FindByPathParts(root, parts, 0);
                if (n != null) return n;
            }
            else
            {
                var n = FindFirstByName(root, key);
                if (n != null) return n;
            }
        }
        return null;
    }

    static GameObject? FindByPathParts(GameObject node, string[] parts, int index)
    {
        if (index >= parts.Length) return null;
        if (!string.Equals(node.Name, parts[index], StringComparison.Ordinal))
            return null;
        if (index == parts.Length - 1) return node;
        foreach (var c in node.Children)
        {
            var r = FindByPathParts(c, parts, index + 1);
            if (r != null) return r;
        }
        return null;
    }

    static GameObject? FindFirstByName(GameObject node, string name)
    {
        if (string.Equals(node.Name, name, StringComparison.Ordinal)) return node;
        foreach (var c in node.Children)
        {
            var r = FindFirstByName(c, name);
            if (r != null) return r;
        }
        return null;
    }
}

public enum TriggerReactionKind
{
    None,
    LoadScene,
    SetObjectEnabled,
    PublishChannel
}
