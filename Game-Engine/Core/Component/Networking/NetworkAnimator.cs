#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Game_Engine.Core.Networking;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Network animator component — synchronizes animation state machine
    /// parameters and state transitions over the network.
    /// Requires both NetworkIdentity and Animator on the same GameObject.
    /// </summary>
    [ComponentCategory("Networking")]
    [Require(typeof(NetworkIdentity), typeof(Animator))]
    public sealed class NetworkAnimator : Behavior
    {
        // ── Configuration ──
        /// <summary>Sync rate for animation parameters (updates per second).</summary>
        [Persist] public float SyncRate { get; set; } = 10f;

        // ── Runtime ──
        private float _syncTimer;
        private string? _lastStateName;
        private readonly Dictionary<string, float> _lastFloatParams = new();
        private readonly Dictionary<string, bool> _lastBoolParams = new();

        public override void Update()
        {
            var identity = GetComponent<NetworkIdentity>();
            var animator = GetComponent<Animator>();
            if (identity == null || animator == null || !NetworkManager.IsActive) return;

            if (identity.HasAuthority)
            {
                // Authority: detect changes and broadcast
                _syncTimer += Time.deltaTime;
                if (_syncTimer >= 1f / SyncRate)
                {
                    _syncTimer = 0f;
                    SyncAnimatorState(animator);
                }
            }
        }

        private void SyncAnimatorState(Animator animator)
        {
            bool changed = false;

            // Check if state changed
            if (animator.CurrentStateName != _lastStateName)
            {
                _lastStateName = animator.CurrentStateName;
                changed = true;
            }

            // Check float parameters
            foreach (var (name, value) in animator.FloatParams)
            {
                if (!_lastFloatParams.TryGetValue(name, out var last) || MathF.Abs(last - value) > 0.01f)
                {
                    _lastFloatParams[name] = value;
                    changed = true;
                }
            }

            // Check bool parameters
            foreach (var (name, value) in animator.BoolParams)
            {
                if (!_lastBoolParams.TryGetValue(name, out var last) || last != value)
                {
                    _lastBoolParams[name] = value;
                    changed = true;
                }
            }

            if (changed)
                BroadcastAnimatorData(animator);
        }

        private void BroadcastAnimatorData(Animator animator)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // State name
            bw.Write(animator.CurrentStateName ?? "");
            bw.Write(animator.StateTime);

            // Float parameters
            bw.Write(animator.FloatParams.Count);
            foreach (var (name, value) in animator.FloatParams)
            {
                bw.Write(name);
                bw.Write(value);
            }

            // Bool parameters
            bw.Write(animator.BoolParams.Count);
            foreach (var (name, value) in animator.BoolParams)
            {
                bw.Write(name);
                bw.Write(value);
            }

            var identity = GetComponent<NetworkIdentity>();
            if (identity != null)
            {
                NetworkManager.SendRPCAll($"AnimSync_{identity.NetworkId}", ms.ToArray());
            }
        }

        public override void Start()
        {
            var identity = GetComponent<NetworkIdentity>();
            if (identity != null)
            {
                NetworkManager.RegisterRPC($"AnimSync_{identity.NetworkId}", OnAnimSyncReceived);
            }
        }

        private void OnAnimSyncReceived(int peerId, byte[] data)
        {
            var identity = GetComponent<NetworkIdentity>();
            if (identity == null || identity.HasAuthority) return; // Don't apply our own updates

            var animator = GetComponent<Animator>();
            if (animator == null) return;

            try
            {
                using var ms = new MemoryStream(data);
                using var br = new BinaryReader(ms);

                string stateName = br.ReadString();
                float stateTime = br.ReadSingle();

                // Apply state
                if (!string.IsNullOrEmpty(stateName) && stateName != animator.CurrentStateName)
                    animator.Play(stateName, 0.1f);

                // Apply float parameters
                int floatCount = br.ReadInt32();
                for (int i = 0; i < floatCount; i++)
                {
                    string name = br.ReadString();
                    float value = br.ReadSingle();
                    animator.SetFloat(name, value);
                }

                // Apply bool parameters
                int boolCount = br.ReadInt32();
                for (int i = 0; i < boolCount; i++)
                {
                    string name = br.ReadString();
                    bool value = br.ReadBoolean();
                    animator.SetBool(name, value);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[NetworkAnimator] Sync error: {ex.Message}");
            }
        }
    }
}
