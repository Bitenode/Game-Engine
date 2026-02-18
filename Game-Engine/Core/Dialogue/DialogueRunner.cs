#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core;
using Game_Engine.Core.Events;

namespace Game_Engine.Core.Dialogue
{
    /// <summary>
    /// Controls how dialogue is presented: text subtitles, voice audio, or both.
    /// </summary>
    public enum DialogueMode
    {
        TextOnly,
        VoiceOnly,
        TextAndVoice
    }

    // ── Dialogue Events (published via EventBus) ──

    public struct DialogueStartedEvent
    {
        public string TreeName { get; set; }
    }

    public struct DialogueLineEvent
    {
        public string Speaker { get; set; }
        public string Text { get; set; }
        public float Duration { get; set; }
        public string NodeId { get; set; }
        public string VoiceClipPath { get; set; }
        public bool ShowText { get; set; }
        public bool PlayVoice { get; set; }
    }

    public struct DialogueChoiceEvent
    {
        public List<DialogueChoiceOption> Options { get; set; }
        public string NodeId { get; set; }
    }

    public struct DialogueChoiceOption
    {
        public int Index { get; set; }
        public string Text { get; set; }
    }

    public struct DialogueEndedEvent
    {
        public string TreeName { get; set; }
    }

    /// <summary>
    /// Component that walks a DialogueTree, publishing events for UI display.
    /// </summary>
    [ComponentCategory("Dialogue")]
    public sealed class DialogueRunner : Behavior
    {
        /// <summary>The dialogue tree to run.</summary>
        public DialogueTree? Tree { get; set; }

        /// <summary>Variable store for conditions and actions.</summary>
        public DialogueVariableStore Variables { get; set; } = new();

        // ── Voice / Text Mode ──
        /// <summary>Dialogue presentation mode: text, voice, or both.</summary>
        [Persist] public DialogueMode Mode { get; set; } = DialogueMode.TextAndVoice;

        /// <summary>Volume for voice line playback (0-1).</summary>
        [Persist] public float VoiceVolume { get; set; } = 1f;

        /// <summary>Whether to auto-advance when a voice clip finishes (instead of waiting for input).</summary>
        [Persist] public bool AutoAdvanceOnVoiceEnd { get; set; } = true;

        /// <summary>Whether dialogue is currently active.</summary>
        public bool IsRunning { get; private set; }

        /// <summary>Whether we're waiting for player input (advance or choice).</summary>
        public bool IsWaitingForInput { get; private set; }

        /// <summary>The current node being displayed.</summary>
        public DialogueNode? CurrentNode { get; private set; }

        // Timer for auto-advance
        private float _displayTimer;
        private bool _autoAdvance;

        // Voice playback
        private AudioHandle? _voiceHandle;

        /// <summary>Start the dialogue from the beginning.</summary>
        public void StartDialogue()
        {
            if (Tree == null) return;

            IsRunning = true;
            EventBus.Publish(new DialogueStartedEvent { TreeName = Tree.Name });

            var startNode = Tree.GetStartNode();
            if (startNode != null)
            {
                if (startNode.Type == DialogueNodeType.Start)
                    AdvanceToNode(startNode.NextNodeId);
                else
                    ProcessNode(startNode);
            }
        }

        /// <summary>Start with a specific dialogue tree.</summary>
        public void StartDialogue(DialogueTree tree)
        {
            Tree = tree;
            StartDialogue();
        }

        /// <summary>Stop the dialogue immediately.</summary>
        public void StopDialogue()
        {
            StopVoice();
            IsRunning = false;
            IsWaitingForInput = false;
            CurrentNode = null;
            EventBus.Publish(new DialogueEndedEvent { TreeName = Tree?.Name ?? "" });
        }

        /// <summary>Stop any currently playing voice line.</summary>
        public void StopVoice()
        {
            if (_voiceHandle != null)
            {
                _voiceHandle.Stop();
                _voiceHandle = null;
            }
        }

        /// <summary>Whether a voice clip is currently playing.</summary>
        public bool IsVoicePlaying => _voiceHandle != null && _voiceHandle.IsPlaying;

        /// <summary>Advance to the next node (call when player presses continue).</summary>
        public void Advance()
        {
            if (!IsRunning || !IsWaitingForInput || CurrentNode == null) return;
            if (CurrentNode.Type == DialogueNodeType.Choice) return; // Must use SelectChoice

            StopVoice();
            IsWaitingForInput = false;
            AdvanceToNode(CurrentNode.NextNodeId);
        }

        /// <summary>Select a choice by index.</summary>
        public void SelectChoice(int choiceIndex)
        {
            if (!IsRunning || CurrentNode == null || CurrentNode.Type != DialogueNodeType.Choice) return;

            var availableChoices = GetAvailableChoices(CurrentNode);
            if (choiceIndex < 0 || choiceIndex >= availableChoices.Count) return;

            IsWaitingForInput = false;
            AdvanceToNode(availableChoices[choiceIndex].NextNodeId);
        }

        public override void Update()
        {
            if (!IsRunning) return;

            // Auto-advance when voice clip finishes (if enabled and voice was playing)
            if (AutoAdvanceOnVoiceEnd && _voiceHandle != null && !_voiceHandle.IsPlaying && IsWaitingForInput)
            {
                _voiceHandle = null;
                IsWaitingForInput = false;
                if (CurrentNode != null)
                    AdvanceToNode(CurrentNode.NextNodeId);
                return;
            }

            if (!_autoAdvance) return;

            _displayTimer -= Time.deltaTime;
            if (_displayTimer <= 0f)
            {
                _autoAdvance = false;
                if (CurrentNode != null)
                    AdvanceToNode(CurrentNode.NextNodeId);
            }
        }

        private void AdvanceToNode(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || Tree == null)
            {
                StopDialogue();
                return;
            }

            var node = Tree.GetNode(nodeId);
            if (node == null)
            {
                StopDialogue();
                return;
            }

            ProcessNode(node);
        }

        private void ProcessNode(DialogueNode node)
        {
            CurrentNode = node;
            StopVoice();

            // Execute variable actions
            foreach (var action in node.Actions)
            {
                if (!string.IsNullOrEmpty(action.Variable))
                    Variables.Set(action.Variable, action.Value);
            }

            switch (node.Type)
            {
                case DialogueNodeType.Dialogue:
                    bool showText = Mode != DialogueMode.VoiceOnly;
                    bool playVoice = Mode != DialogueMode.TextOnly && !string.IsNullOrEmpty(node.VoiceClipPath);

                    EventBus.Publish(new DialogueLineEvent
                    {
                        Speaker = node.Speaker,
                        Text = node.Text,
                        Duration = node.Duration,
                        NodeId = node.Id,
                        VoiceClipPath = node.VoiceClipPath,
                        ShowText = showText,
                        PlayVoice = playVoice
                    });

                    // Play the voice clip
                    if (playVoice)
                        _voiceHandle = AudioBackend.Play(node.VoiceClipPath, VoiceVolume, 1f, false);

                    if (node.Duration > 0f)
                    {
                        _displayTimer = node.Duration;
                        _autoAdvance = true;
                        IsWaitingForInput = false;
                    }
                    else if (playVoice && AutoAdvanceOnVoiceEnd)
                    {
                        // Wait for voice clip to finish, then auto-advance
                        _autoAdvance = false;
                        IsWaitingForInput = true;
                    }
                    else
                    {
                        _autoAdvance = false;
                        IsWaitingForInput = true;
                    }
                    break;

                case DialogueNodeType.Choice:
                    var choices = GetAvailableChoices(node);
                    var options = new List<DialogueChoiceOption>();
                    for (int i = 0; i < choices.Count; i++)
                        options.Add(new DialogueChoiceOption { Index = i, Text = choices[i].Text });

                    EventBus.Publish(new DialogueChoiceEvent { Options = options, NodeId = node.Id });
                    IsWaitingForInput = true;
                    _autoAdvance = false;
                    break;

                case DialogueNodeType.Branch:
                    bool condition = Variables.CheckCondition(node.BranchVariable, node.BranchValue);
                    AdvanceToNode(condition ? node.TrueNextId : node.FalseNextId);
                    break;

                case DialogueNodeType.End:
                    StopDialogue();
                    break;

                case DialogueNodeType.Start:
                    AdvanceToNode(node.NextNodeId);
                    break;
            }
        }

        private List<DialogueChoice> GetAvailableChoices(DialogueNode node)
        {
            var available = new List<DialogueChoice>();
            foreach (var choice in node.Choices)
            {
                if (string.IsNullOrEmpty(choice.ConditionVariable) ||
                    Variables.CheckCondition(choice.ConditionVariable, choice.ConditionValue))
                {
                    available.Add(choice);
                }
            }
            return available;
        }
    }
}
