namespace DesktopJumpList.Models;

public enum DockEdge
{
    Left,
    Right
}

public enum AppTheme
{
    /// <summary>Follow the Windows app theme.</summary>
    System,
    Light,
    Dark
}

/// <summary>Persisted to settings.json. Everything here is local-only.</summary>
public sealed class AppSettings
{
    /// <summary>Which screen edge the ball rests against.</summary>
    public DockEdge Edge { get; set; } = DockEdge.Right;

    /// <summary>Vertical position as a 0..1 ratio of the work area, so it survives resolution changes.</summary>
    public double EdgeRatio { get; set; } = 0.62;

    /// <summary>Idle delay before the ball tucks itself into the edge.</summary>
    public int PeekDelayMs { get; set; } = 2200;

    /// <summary>Global toggle hotkey, stored as "Ctrl+Alt+J" style text.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+J";

    /// <summary>Colour mode: follow system, or force light/dark.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    public bool FirstRunDone { get; set; }

    public AppSettings Clone() => new()
    {
        Edge = Edge,
        EdgeRatio = EdgeRatio,
        PeekDelayMs = PeekDelayMs,
        Hotkey = Hotkey,
        Theme = Theme,
        FirstRunDone = FirstRunDone
    };
}
