using System;
using Avalonia.Controls;

namespace Game_Engine.Docking;

public sealed class ToolWindow : Window
{
    private readonly Action _onUserClose;
    private bool _suppress;

    public ToolWindow(string title, Control content, Action onUserClose)
    {
        Title = title;
        Content = content;
        _onUserClose = onUserClose;
        Width = 600; Height = 400;

        Closed += (_, __) =>
        {
            // If user clicked X, tell manager to REMOVE the panel.
            // (When docking programmatically we call CloseFromManager() which sets _suppress.)
            if (!_suppress)
                _onUserClose();
        };
    }

    /// <summary>Close without invoking user-close callback.</summary>
    public void CloseFromManager()
    {
        _suppress = true;
        Close();
    }
}
