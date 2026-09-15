using System;
using System.Collections.Generic;
using Game_Engine.Core.Component.UI;

namespace Game_Engine.Views.Inspector;

/// <summary>Property names drawn in the custom UI sections (also marked <c>[HideInInspector]</c> on the components).</summary>
static class UIElementInspector
{
    public static readonly HashSet<string> BaseDeferred = new(StringComparer.Ordinal)
    {
        nameof(UIElement.Raycastable),
        nameof(UIElement.Color),
        nameof(UIElement.Opacity),
        nameof(UIElement.Focusable),
        nameof(UIElement.OpacityTransitionSpeed),
        nameof(UIElement.OpacityTargetEnabled),
        nameof(UIElement.OpacityTarget),
    };

    public static readonly HashSet<string> ButtonDeferred = new(StringComparer.Ordinal)
    {
        nameof(UIButton.Interactable),
        nameof(UIButton.NormalColor),
        nameof(UIButton.HighlightedColor),
        nameof(UIButton.PressedColor),
        nameof(UIButton.DisabledColor),
        nameof(UIButton.FadeDuration),
    };

    public static readonly HashSet<string> SliderDeferred = new(StringComparer.Ordinal)
    {
        nameof(UISlider.MinValue),
        nameof(UISlider.MaxValue),
        nameof(UISlider.Value),
        nameof(UISlider.WholeNumbers),
        nameof(UISlider.Interactable),
        nameof(UISlider.StepSize),
        nameof(UISlider.Direction),
        nameof(UISlider.BackgroundColor),
        nameof(UISlider.FillColor),
        nameof(UISlider.HandleColor),
        nameof(UISlider.HandleSize),
    };

    public static readonly HashSet<string> ToggleDeferred = new(StringComparer.Ordinal)
    {
        nameof(UIToggle.IsOn),
        nameof(UIToggle.Interactable),
        nameof(UIToggle.BackgroundColor),
        nameof(UIToggle.ActiveColor),
        nameof(UIToggle.CheckmarkColor),
        nameof(UIToggle.CheckmarkInset),
        nameof(UIToggle.HoverBackgroundColor),
    };

    public static readonly HashSet<string> InputFieldDeferred = new(StringComparer.Ordinal)
    {
        nameof(UIInputField.Text),
        nameof(UIInputField.Placeholder),
        nameof(UIInputField.CharacterLimit),
        nameof(UIInputField.ContentType),
        nameof(UIInputField.FontSize),
        nameof(UIInputField.FontPath),
        nameof(UIInputField.ReadOnly),
        nameof(UIInputField.BackgroundColor),
        nameof(UIInputField.TextColor),
        nameof(UIInputField.PlaceholderColor),
        nameof(UIInputField.CursorColor),
        nameof(UIInputField.SelectionColor),
        nameof(UIInputField.FocusedBorderColor),
        nameof(UIInputField.FocusBorderWidth),
        nameof(UIInputField.DeselectOnClickOutside),
    };

    public static readonly HashSet<string> ProgressBarDeferred = new(StringComparer.Ordinal)
    {
        nameof(UIProgressBar.MinValue),
        nameof(UIProgressBar.MaxValue),
        nameof(UIProgressBar.Direction),
        nameof(UIProgressBar.BackgroundColor),
        nameof(UIProgressBar.FillColor),
    };
}
