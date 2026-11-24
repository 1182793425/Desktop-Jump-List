using System.Text.Json.Serialization;

namespace DesktopJumpList.Models;

public enum DockEdge
{
    /// <summary>Freely placed — keeps the last dropped position on any screen.</summary>
    None,
    Left,
    Right,
    Top,
    Bottom
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
    /// <summary>Which edge the ball rests against, or None for free placement.</summary>
    public DockEdge Edge { get; set; } = DockEdge.Right;

    /// <summary>Position along the edge as a 0..1 ratio (vertical for Left/Right, horizontal for Top/Bottom).</summary>
    public double EdgeRatio { get; set; } = 0.62;

    /// <summary>Absolute screen position used while <see cref="Edge"/> is None (-1 = not set yet).</summary>
    public double FreeLeft { get; set; } = -1;
    public double FreeTop { get; set; } = -1;

    /// <summary>Idle delay before the ball tucks itself into the edge.</summary>
    public int PeekDelayMs { get; set; } = 2200;

    /// <summary>Global toggle hotkey, stored as "Ctrl+Alt+J" style text.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+J";

    /// <summary>Colour mode: follow system, or force light/dark. Named "themeMode" on disk so
    /// the old v2 "theme" (Light/Dark only) never collides and the default stays System.</summary>
    [JsonPropertyName("themeMode")]
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Tiles per row. Clamped to 3..8.</summary>
    public int Columns { get; set; } = 5;

    /// <summary>Maximum visible rows before the panel scrolls. Clamped to 2..8.</summary>
    public int MaxRows { get; set; } = 4;

    /// <summary>How close (px) a dropped ball must be to a screen edge to snap to it.</summary>
    public int SnapDistance { get; set; } = 40;

    public bool FirstRunDone { get; set; }

    public AppSettings Clone() => new()
    {
        Edge = Edge,
        EdgeRatio = EdgeRatio,
        FreeLeft = FreeLeft,
        FreeTop = FreeTop,
        PeekDelayMs = PeekDelayMs,
        Hotkey = Hotkey,
        Theme = Theme,
        Columns = Columns,
        MaxRows = MaxRows,
        SnapDistance = SnapDistance,
        FirstRunDone = FirstRunDone
    };
}
