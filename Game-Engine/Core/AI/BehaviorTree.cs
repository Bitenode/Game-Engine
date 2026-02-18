#nullable enable
using System;

namespace Game_Engine.Core.AI
{
    /// <summary>
    /// A behavior tree asset — a root node that can be ticked each frame.
    /// </summary>
    public sealed class BehaviorTree
    {
        /// <summary>Display name of this tree.</summary>
        public string Name { get; set; } = "New Behavior Tree";

        /// <summary>The root node of this tree.</summary>
        public BTNode? Root { get; set; }

        /// <summary>Tick the tree once. Returns the status of the root node.</summary>
        public BTStatus Tick(Blackboard blackboard, float deltaTime)
        {
            if (Root == null) return BTStatus.Failure;
            return Root.Tick(blackboard, deltaTime);
        }

        /// <summary>Reset all nodes in the tree to their initial state.</summary>
        public void Reset()
        {
            Root?.Reset();
        }

        // ── Builder helpers for constructing trees in code ──

        /// <summary>Create a simple sequence tree.</summary>
        public static BehaviorTree Sequence(string name, params BTNode[] children)
        {
            var seq = new SequenceNode { Name = name };
            seq.Children.AddRange(children);
            return new BehaviorTree { Name = name, Root = seq };
        }

        /// <summary>Create a simple selector tree.</summary>
        public static BehaviorTree Selector(string name, params BTNode[] children)
        {
            var sel = new SelectorNode { Name = name };
            sel.Children.AddRange(children);
            return new BehaviorTree { Name = name, Root = sel };
        }
    }
}
