#nullable enable
using System;

namespace Game_Engine.Core.AI
{
    /// <summary>
    /// Component that ticks a BehaviorTree each frame.
    /// Attach to a GameObject to give it AI behavior.
    /// </summary>
    [ComponentCategory("AI")]
    public sealed class BehaviorTreeRunner : Behavior
    {
        /// <summary>The behavior tree to execute.</summary>
        public BehaviorTree? Tree { get; set; }

        /// <summary>Per-agent blackboard for sharing data between nodes.</summary>
        public Blackboard Blackboard { get; } = new();

        /// <summary>Whether the tree is currently running.</summary>
        [Persist] public bool IsRunning { get; set; } = true;

        /// <summary>Tick rate in seconds. 0 = every frame.</summary>
        [Persist] public float TickInterval { get; set; } = 0f;

        /// <summary>The result of the last tree tick.</summary>
        public BTStatus LastStatus { get; private set; } = BTStatus.Running;

        /// <summary>Event raised after each tick with the result status.</summary>
        public event Action<BTStatus>? OnTick;

        private float _tickTimer;

        public override void Start()
        {
            // Initialize blackboard with owner reference
            if (gameObject != null)
                Blackboard.Set("Self", gameObject);
        }

        public override void Update()
        {
            if (!IsRunning || Tree == null) return;

            if (TickInterval > 0f)
            {
                _tickTimer += Time.deltaTime;
                if (_tickTimer < TickInterval) return;
                _tickTimer -= TickInterval;
            }

            LastStatus = Tree.Tick(Blackboard, Time.deltaTime);
            OnTick?.Invoke(LastStatus);

            // If the tree completed (Success or Failure), reset for next tick
            if (LastStatus != BTStatus.Running)
                Tree.Reset();
        }

        /// <summary>Restart the tree from scratch.</summary>
        public void Restart()
        {
            Tree?.Reset();
            LastStatus = BTStatus.Running;
            _tickTimer = 0f;
        }

        /// <summary>Set a blackboard value (convenience).</summary>
        public void SetBlackboardValue<T>(string key, T value) => Blackboard.Set(key, value);

        /// <summary>Get a blackboard value (convenience).</summary>
        public T GetBlackboardValue<T>(string key, T defaultValue = default!) => Blackboard.Get(key, defaultValue);
    }
}
