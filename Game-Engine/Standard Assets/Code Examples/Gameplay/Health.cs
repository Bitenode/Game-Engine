using System;
using Game_Engine.Core;
using Game_Engine.Core.Component;

/// <summary>Sample health pool for players and NPCs; works with <see cref="TriggerVolume"/> damage zones via <see cref="IDamageable"/>.</summary>
public sealed class Health : Behavior, IDamageable
{
    [Persist] public float MaxHealth { get; set; } = 100f;
    [Persist] public float Current { get; set; } = 100f;

    public override void OnEnable()
    {
        base.OnEnable();
        Current = Math.Clamp(Current, 0f, Math.Max(0.01f, MaxHealth));
    }

    public void ApplyDamage(float amount)
    {
        if (amount <= 0f) return;
        Current = Math.Max(0f, Current - amount);
    }
}
