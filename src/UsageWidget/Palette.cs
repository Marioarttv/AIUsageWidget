using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace UsageWidget;

// The widget's colours. Dark is the default look (as on macOS); Light and "Match Windows" are chosen in the menu.
internal sealed class Palette
{
    public required bool IsLight { get; init; }
    public required Brush Background, Border, Divider, Ink, Muted, Label, Value, Faint, Track, Warn, Danger, Unknown, Blocked,
        ChipBack, ChipHover, ButtonInk, ButtonHover, IconInk, PinBack, TooltipBack, TooltipBorder, Claude, Codex, Antigravity, OnAccent;

    public Brush Accent(UsageProvider provider) => provider switch { UsageProvider.Claude => Claude, UsageProvider.Codex => Codex, _ => Antigravity };

    private static readonly Dictionary<string, Brush> cache = new();
    internal static Brush Brush(string value)
    {
        lock (cache)
        {
            if (!cache.TryGetValue(value, out var brush)) { brush = (Brush)new BrushConverter().ConvertFromString(value)!; brush.Freeze(); cache[value] = brush; }
            return brush;
        }
    }

    public static readonly Palette Dark = new() {
        IsLight = false, Background = Brush("#F2111720"), Border = Brush("#3A4555"), Divider = Brush("#252E3B"), Ink = Brush("#F0F3F7"),
        Muted = Brush("#9AA6B7"), Label = Brush("#AAB5C5"), Value = Brush("#CAD3DF"), Faint = Brush("#6F7C8E"), Track = Brush("#263140"),
        Warn = Brush("#E4BF7D"), Danger = Brush("#F08989"), Unknown = Brush("#6B798C"), Blocked = Brush("#F2A65A"),
        ChipBack = Brush("#14FFFFFF"), ChipHover = Brush("#29FFFFFF"), ButtonInk = Brush("#D4DCE7"), ButtonHover = Brush("#263244"),
        IconInk = Brush("#8390A2"), PinBack = Brush("#2A3648"), TooltipBack = Brush("#202B3D"), TooltipBorder = Brush("#65758C"),
        Claude = Brush("#D99776"), Codex = Brush("#87BBA7"), Antigravity = Brush("#8FA9F2"), OnAccent = Brush("#D9101620"),
    };

    public static readonly Palette Light = new() {
        IsLight = true, Background = Brush("#F7FAFBFD"), Border = Brush("#CDD5DF"), Divider = Brush("#E1E6EC"), Ink = Brush("#17202B"),
        Muted = Brush("#5A6676"), Label = Brush("#3F4B5B"), Value = Brush("#253040"), Faint = Brush("#7A8595"), Track = Brush("#E1E6ED"),
        Warn = Brush("#9A6400"), Danger = Brush("#C03535"), Unknown = Brush("#9AA4B2"), Blocked = Brush("#C25A00"),
        ChipBack = Brush("#0F17202B"), ChipHover = Brush("#1F17202B"), ButtonInk = Brush("#3F4B5B"), ButtonHover = Brush("#E3E9F0"),
        IconInk = Brush("#5F6B7B"), PinBack = Brush("#DAE3ED"), TooltipBack = Brush("#FFFFFF"), TooltipBorder = Brush("#C3CCD7"),
        Claude = Brush("#B8643F"), Codex = Brush("#2E8466"), Antigravity = Brush("#4767D0"), OnAccent = Brush("#FFFFFF"),
    };

    // "dark" (also when unset), "light", or "system" to follow Windows' app theme.
    public static Palette For(string? theme) => theme switch {
        "light" => Light,
        "system" => SystemUsesLight() ? Light : Dark,
        _ => Dark,
    };

    private static bool SystemUsesLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
    }

    // Styles in App.xaml read these keys, so buttons and tooltips follow the theme.
    public void Apply(ResourceDictionary resources)
    {
        resources["Ink"] = Ink; resources["Muted"] = Muted; resources["ButtonInk"] = ButtonInk; resources["ButtonHover"] = ButtonHover;
        resources["IconInk"] = IconInk; resources["TooltipBack"] = TooltipBack; resources["TooltipBorder"] = TooltipBorder;
    }
}
