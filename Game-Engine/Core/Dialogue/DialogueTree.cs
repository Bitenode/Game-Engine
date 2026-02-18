#nullable enable
using System;
using System.Collections.Generic;

namespace Game_Engine.Core.Dialogue
{
    /// <summary>Type of dialogue node.</summary>
    public enum DialogueNodeType
    {
        Dialogue,
        Choice,
        Branch,
        Start,
        End
    }

    /// <summary>
    /// A single node in a dialogue tree.
    /// </summary>
    public class DialogueNode
    {
        /// <summary>Unique ID for this node.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public DialogueNodeType Type { get; set; } = DialogueNodeType.Dialogue;

        // ── Dialogue properties ──
        /// <summary>Speaker name displayed in the UI.</summary>
        public string Speaker { get; set; } = "";
        /// <summary>Dialogue text content.</summary>
        public string Text { get; set; } = "";
        /// <summary>Duration to display text (0 = wait for input).</summary>
        public float Duration { get; set; } = 0f;
        /// <summary>Path to a voice line audio file (.wav, .mp3, .ogg) for this node.</summary>
        public string VoiceClipPath { get; set; } = "";

        // ── Choice properties ──
        /// <summary>Choices available at this node (for Choice nodes).</summary>
        public List<DialogueChoice> Choices { get; set; } = new();

        // ── Branch properties ──
        /// <summary>Variable name to check for branching.</summary>
        public string BranchVariable { get; set; } = "";
        /// <summary>Expected value for the branch condition.</summary>
        public string BranchValue { get; set; } = "";
        /// <summary>Node ID when condition is true.</summary>
        public string TrueNextId { get; set; } = "";
        /// <summary>Node ID when condition is false.</summary>
        public string FalseNextId { get; set; } = "";

        // ── Navigation ──
        /// <summary>ID of the next node (for linear flow).</summary>
        public string NextNodeId { get; set; } = "";

        // ── Actions ──
        /// <summary>Variable assignments to execute when this node is entered.</summary>
        public List<VariableAction> Actions { get; set; } = new();

        // ── Editor ──
        /// <summary>Editor position for node graph layout.</summary>
        public float EditorX { get; set; }
        public float EditorY { get; set; }
    }

    /// <summary>A choice option in a dialogue choice node.</summary>
    public class DialogueChoice
    {
        /// <summary>Text displayed for this choice.</summary>
        public string Text { get; set; } = "";
        /// <summary>ID of the node to go to when this choice is selected.</summary>
        public string NextNodeId { get; set; } = "";
        /// <summary>Optional condition variable that must be true to show this choice.</summary>
        public string ConditionVariable { get; set; } = "";
        /// <summary>Expected value for the condition (empty = just check existence/true).</summary>
        public string ConditionValue { get; set; } = "";
    }

    /// <summary>Variable assignment action executed when entering a node.</summary>
    public class VariableAction
    {
        public string Variable { get; set; } = "";
        public string Value { get; set; } = "";
    }

    /// <summary>
    /// A dialogue tree asset — a graph of dialogue nodes.
    /// </summary>
    public class DialogueTree
    {
        public string Name { get; set; } = "New Dialogue";
        public string StartNodeId { get; set; } = "";
        public List<DialogueNode> Nodes { get; set; } = new();

        /// <summary>Find a node by ID.</summary>
        public DialogueNode? GetNode(string id)
        {
            for (int i = 0; i < Nodes.Count; i++)
                if (Nodes[i].Id == id) return Nodes[i];
            return null;
        }

        /// <summary>Get the start node.</summary>
        public DialogueNode? GetStartNode()
        {
            if (!string.IsNullOrEmpty(StartNodeId))
                return GetNode(StartNodeId);
            // Fallback: first Start-type node, or first node
            for (int i = 0; i < Nodes.Count; i++)
                if (Nodes[i].Type == DialogueNodeType.Start) return Nodes[i];
            return Nodes.Count > 0 ? Nodes[0] : null;
        }

        /// <summary>Add a dialogue node.</summary>
        public DialogueNode AddNode(DialogueNodeType type, string speaker = "", string text = "")
        {
            var node = new DialogueNode { Type = type, Speaker = speaker, Text = text };
            Nodes.Add(node);
            return node;
        }

        /// <summary>Remove a node by ID.</summary>
        public bool RemoveNode(string id) => Nodes.RemoveAll(n => n.Id == id) > 0;
    }
}
