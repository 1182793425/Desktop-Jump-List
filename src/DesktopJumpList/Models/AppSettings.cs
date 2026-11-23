namespace DesktopJumpList.Models;

public enum AppTheme
{
    System,
    Light,
    Dark
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public string Edge { get; set; } = "Right";
    public double TopRatio { get; set; } = 0.5;
    public bool HasPlacedWindow { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Alt+J";
    public bool ShowHints { get; set; } = true;

    public AppSettings Clone() => new()
    {
        Theme = Theme,
        Edge = Edge,
        TopRatio = TopRatio,
        HasPlacedWindow = HasPlacedWindow,
        Hotkey = Hotkey,
        ShowHints = ShowHints
    };
}
