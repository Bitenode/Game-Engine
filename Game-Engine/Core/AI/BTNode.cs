#nullable enable
using System;
using System.Collections.Generic;

namespace Game_Engine.Core.AI
{
    /// <summary>Return status of a behavior tree node tick.</summary>
    public enum BTStatus
    {
        Running,
        Success,
        Failure
    }

    /// <summary>Base class for all behavior tree nodes.</summary>
    public abstract class BTNode
    {
        public string Name { get; set; } = "";

        /// <summary>Tick this node. Called every frame while the tree is active.</summary>
        public abstract BTStatus Tick(Blackboard blackboard, float deltaTime);

        /// <summary>Reset internal state when the node is re-entered.</summary>
        public virtual void Reset() { }
    }

    // ── Composite Nodes ──

    /// <summary>
    /// Selector (OR): Ticks children left-to-right. Succeeds on first child success.
    /// Fails only if all children fail.
    /// </summary>
    public class SelectorNode : BTNode
    {
        public List<BTNode> Children { get; set; } = new();
        private int _currentChild;

        public override BTStatus Tick(Blackboard blackboard, float deltaTime)
        {
            for (int i = _currentChild; i < Children.Count; i++)
            {
                var status = Children[i].Tick(blackboard, deltaTime);
                if (status == BTStatus.Running)
                {
                    _currentChild = i;
                    return BTStatus.Running;
                }
                if (status == BTStatus.Success)
                {
                    _currentChild = 0;
                    return BTStatus.Success;
                }
            }
            _currentChild = 0;
            return BTStatus.Failure;
        }

        public override void Reset()
        {
            _currentChild = 0;
            foreach (var child in Children) child.Reset();
        }
    }

    /// <summary>
    /// Sequence (AND): Ticks children left-to-right. Fails on first child failure.
    /// Succeeds only if all children succeed.
    /// </summary>
    public class SequenceNode : BTNode
    {
        public List<BTNode> Children { get; set; } = new();
        private int _currentChild;

        public override BTStatus Tick(Blackboard blackboard, float deltaTime)
        {
            for (int i = _currentChild; i < Children.Count; i++)
            {
                var status = Children[i].Tick(blackboard, deltaTime);
                if (status == BTStatus.Running)
                {
                    _currentChild = i;
                    return BTStatus.Running;
                }
                if (status == BTStatus.Failure)
                {
                    _currentChild = 0;
                    return BTStatus.Failure;
                }
            }
            _currentChild = 0;
            return BTStatus.Success;
        }

        public override void Reset()
        {
            _currentChild = 0;
            foreach (var child in Children) child.Reset();
        }
    }

    /// <summary>
    /// Parallel: Ticks all children every frame.
    /// Succeeds when RequiredSuccesses children succeed. Fails when enough fail that success is impossible.
    /// </summary>
    public class ParallelNode : BTNode
    {
        public List<BTNode> Children { get; set; } = new();
        /// <summary>Number of children that must succeed for the node to succeed.</summary>
        public int RequiredSuccesses { get; set; } = 1;

        public override BTStatus Tick(Blackboard blackboard, float deltaTime)
        {
            int successes = 0, failures = 0;
            foreach (var child in Children)
            {
                var status = child.Tick(blackboard, deltaTime);
                if (status == BTStatus.Success) successes++;
                else if (status == BTStatus.Failure) failures++;
            }

            if (successes >= RequiredSuccesses) return BTStatus.Success;
            if (failures > Children.Count - RequiredSuccesses) return BTStatus.Failure;
            return BTStatus.Running;
        }

        public override void Reset()
        {
            foreach (var child in Children) child.Reset();
        }
    }

    // ── Decorator Nodes ──

    /// <summary>Inverts the result of its child (Success -> Failure, Failure -> Success).</summary>
    public class InverterNode : BTNode
    {
        public BTNode? Child { get; set; }

        public override BTStatus Tick(Blackboard blackboard, float deltaTime)
        {
            if (Child == null) return BTStatus.Failure;
            var status = Child.Tick(blackboard, deltaTime);
            return status switch
            {
                BTStatus.Success => BTStatus.Failure,
                BTStatus.Failure => BTStatus.Success,
                _ => BTStatus.Running
            };
        }

        public override void Reset() => Child?.Reset();
    }

    /// <summary>Repeats its child a specified number of times (or forever if Count &lt; 0).</summary>
    public class RepeaterNode : BTNode
    {
        public BTNode? Child { get; set; }
        /// <summary>Number of times to repeat. Negative = infinite.</summary>
        public int Count { get; set; } = -1;
        private int _iterations;

        public override BTStatus Tick(Blackboard blackboard, float deltaTime)
        {
            if (Child == null) return BTStatus.Failure;

            var status = Child.Tick(blackboard, deltaTime);
            if (status == BTStatus.Running) return BTStatus.Running;

            _iterations++;
            if (Count >= 0 && _iterations >= Count)
            {
                _iterations = 0;
                return BTStatus.Success;
            }

            Child.Reset();
            return BTStatus.Running;
        }

        public override void Reset()
        {
            _iterations = 0;
            Child?.Reset();
        }
    }

    /// <summary>Always succeeds regardless of child result (unless Running).</summary>
    public class SucceederNode : BTNode
    {
        public BTNode? Child { get; set; }

        public override BTStatus Tick(Blackboard blackboard, float deltaTime)
        {
            if (Child == null) return BTStatus.Success;
            var status = Child.Tick(blackboard, deltaTime);
            return status == BTStatus.Running ? BTStatus.Running : BTStatus.Success;
        }

        public override void Reset() => Child?.Reset();
    }

    // ── Leaf Nodes ──

    /// <summary>
    /// Action node: executes a delegate and returns its status.
    /// </summary>
    public class ActionNode : BTNode
    {
        public Func<Blackboard, float, BTStatus>? Action { get; set; }

        public ActionNode() { }
        public ActionNode(string name, Func<Blackboard, float, BTStatus> action)
        {
            Name = name;
            Action = action;
        }

        public override BTStatus Tick(Blackboard blackboard, float deltaTime)
        {
            return Action?.Invoke(blackboard, deltaTime) ?? BTStatus.Failure;
        }
    }

    /// <summary>
    /// Condition node: checks a predicate. Returns Success if true, Failure if false.
    /// </summary>
    public class ConditionNode : BTNode
    {
        public Func<Blackboard, bool>? Condition { get; set; }

        public ConditionNode() { }
        public ConditionNode(string name, Func<Blackboard, bool> condition)
        {
            Name = name;
            Condition = condition;
        }

        public override BTStatus Tick(Blackboard blackboard, float deltaTime)
        {
            return (Condition?.Invoke(blackboard) ?? false) ? BTStatus.Success : BTStatus.Failure;
        }
    }

    // ── Built-in Action Nodes ──

    /// <summary>Wait for a specified duration before succeeding.</summary>
    public class WaitNode : BTNode
    {
        public float Duration { get; set; } = 1f;
        private float _elapsed;

        public WaitNode() { Name = "Wait"; }
        public WaitNode(float duration) { Name = "Wait"; Duration = duration; }

        public override BTStatus Tick(Blackboard blackboard, float deltaTime)
        {
            _elapsed += deltaTime;
            if (_elapsed >= Duration)
            {
                _elapsed = 0f;
                return BTStatus.Success;
            }
            return BTStatus.Running;
        }

        public override void Reset() => _elapsed = 0f;
    }
}
