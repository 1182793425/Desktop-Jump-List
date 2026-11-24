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

    /// <summary>Global hotkey is off by default; the tray icon and floating circle are primary.</summary>
    public bool HotkeyEnabled { get; set; }

    /// <summary>Colour mode: follow system, or force light/dark. Named "themeMode" on disk so
    /// the old v2 "theme" (Light/Dark only) never collides and the default stays System.</summary>
    [JsonPropertyName("themeMode")]
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Show the floating circle. Off means tray/hotkey only.</summary>
    public bool ShowDock { get; set; } = true;

    /// <summary>Diameter of the floating circle in DIPs (32..64).</summary>
    public int DockSize { get; set; } = 48;

    /// <summary>Fill colour of the floating circle, as "#RRGGBB".</summary>
    public string DockColor { get; set; } = "#FFFFFF";

    /// <summary>Tile icon size in DIPs (32..48).</summary>
    public int PanelIconSize { get; set; } = 40;

    /// <summary>Tile label font size in DIPs (10..14).</summary>
    public int PanelFontSize { get; set; } = 12;

    /// <summary>Space between the tile icon and its label, and extra row breathing room (2..10).</summary>
    public int PanelGap { get; set; } = 8;

    /// <summary>How many recently-used entries to keep/show (4..20).</summary>
    public int RecentCount { get; set; } = 10;

    /// <summary>Panel tint alpha (0.50..1.00). The desktop shows through as it drops.</summary>
    public double PanelOpacity { get; set; } = 0.85;

    /// <summary>Tiles per row. Clamped to 3..5 so a 100 px tile never overflows the 530 px panel.</summary>
    public int Columns { get; set; } = 5;

    /// <summary>Maximum visible rows before the panel scrolls. Clamped to 2..5 to fit the panel.</summary>
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
        HotkeyEnabled = HotkeyEnabled,
        Theme = Theme,
        ShowDock = ShowDock,
        DockSize = DockSize,
        DockColor = DockColor,
        PanelIconSize = PanelIconSize,
        PanelFontSize = PanelFontSize,
        PanelGap = PanelGap,
        RecentCount = RecentCount,
        PanelOpacity = PanelOpacity,
        Columns = Columns,
        MaxRows = MaxRows,
        SnapDistance = SnapDistance,
        FirstRunDone = FirstRunDone
    };
}
