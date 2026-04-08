namespace Game_Engine.Core.Component;

/// <summary>Implemented by behaviors that receive damage from trigger volumes and weapons.</summary>
public interface IDamageable
{
    void ApplyDamage(float amount);
}
