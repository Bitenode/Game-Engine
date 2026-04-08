#nullable enable
using Game_Engine.Core;
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Events;

/// <summary>Published on <see cref="EventBus"/> when a <see cref="TriggerVolume"/> fires a designer-configured channel.</summary>
public sealed class TriggerVolumeSignal
{
    public required string Channel { get; init; }
    public GameObject? InstigatorObject { get; init; }
    public Collider? InstigatorCollider { get; init; }
}
