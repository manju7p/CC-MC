using System.Windows;
using System.Windows.Controls;

namespace CCMC.Desktop.Controls;

/// <summary>
/// Semantic role for a <see cref="StatusChip"/> - maps to the Success/Warning/Danger/Info/
/// Neutral chip styles in Styles/Theme.xaml. Chosen by each caller based on the actual
/// status being shown (transaction status, device state, sync state, online/offline) -
/// this enum carries no business logic of its own.
/// </summary>
public enum ChipKind
{
    Success,
    Warning,
    Danger,
    Info,
    Neutral,
}

/// <summary>
/// Builds a small icon+text "chip" (Border containing a StackPanel) for showing status
/// consistently across windows - Reception History rows, Sources/Vehicles status, device
/// connection state, sync state, the offline banner. Centralized here (per this redesign's
/// "create reusable WPF resources/styles/templates rather than duplicating styling across
/// every XAML file" instruction) instead of five windows each building their own coloured
/// Border. Always icon + text + colour together - never colour alone, per this task's own
/// accessibility requirement (section 13/20).
/// </summary>
public static class StatusChip
{
    // Segoe Fluent Icons / Segoe MDL2 Assets codepoints, spelled out as explicit \uXXXX
    // escapes (not literal glyph characters) so the source file stays plain ASCII and
    // cannot be silently corrupted by a non-Unicode-aware text tool.
    private const string GlyphCheckMark = "";
    private const string GlyphWarning = "";
    private const string GlyphStatusErrorFull = "";
    private const string GlyphInfo = "";

    public static Border Create(string text, ChipKind kind, string? glyphOverride = null)
    {
        var (borderKey, textKey, iconKey, defaultGlyph) = kind switch
        {
            ChipKind.Success => ("SuccessChipBorder", "SuccessChipText", "SuccessChipIcon", GlyphCheckMark),
            ChipKind.Warning => ("WarningChipBorder", "WarningChipText", "WarningChipIcon", GlyphWarning),
            ChipKind.Danger => ("DangerChipBorder", "DangerChipText", "DangerChipIcon", GlyphStatusErrorFull),
            ChipKind.Info => ("InfoChipBorder", "InfoChipText", "InfoChipIcon", GlyphInfo),
            ChipKind.Neutral => ("NeutralChipBorder", "NeutralChipText", "NeutralChipIcon", GlyphInfo),
            _ => ("NeutralChipBorder", "NeutralChipText", "NeutralChipIcon", GlyphInfo),
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Style = (Style)System.Windows.Application.Current.FindResource(iconKey),
            Text = glyphOverride ?? defaultGlyph,
        });
        panel.Children.Add(new TextBlock
        {
            Style = (Style)System.Windows.Application.Current.FindResource(textKey),
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border
        {
            Style = (Style)System.Windows.Application.Current.FindResource(borderKey),
            Child = panel,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }
}
