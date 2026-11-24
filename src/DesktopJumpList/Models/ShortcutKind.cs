namespace DesktopJumpList.Models;

/// <summary>What a shortcut points at. Drives icon choice and launch strategy.</summary>
public enum ShortcutKind
{
    File,
    Folder,
    App,
    Url,
    /// <summary>A Microsoft Store (UWP/packaged) app, identified by its AppUserModelId.</summary>
    AppX
}
