using MudBlazor;

namespace Hearth.UI.State;

/// <summary>MudBlazor's theme, tuned to feel like Material 3: rounded shapes, sentence-case buttons, the system
/// font. Colours are not set here: hearth.css points MudBlazor's palette variables at Hearth's own theme variables,
/// which Settings → Appearance changes (see hearth.js), so every theme and mode applies to MudBlazor too.</summary>
public static class HearthMudTheme
{
    private static readonly string[] Font =
        ["-apple-system", "BlinkMacSystemFont", "Segoe UI Variable", "Segoe UI", "Inter", "Roboto", "Helvetica Neue", "sans-serif"];

    public static readonly MudTheme Theme = new()
    {
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "12px",
            DrawerWidthLeft = "264px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Font, FontSize = "0.94rem", LetterSpacing = "normal" },
            Button = new ButtonTypography { FontFamily = Font, FontWeight = "500", TextTransform = "none", LetterSpacing = ".01em", FontSize = ".875rem" },
            Body1 = new Body1Typography { FontFamily = Font, FontSize = ".94rem" },
            Body2 = new Body2Typography { FontFamily = Font, FontSize = ".875rem" },
            Caption = new CaptionTypography { FontFamily = Font },
            Subtitle1 = new Subtitle1Typography { FontFamily = Font },
            Subtitle2 = new Subtitle2Typography { FontFamily = Font },
            H5 = new H5Typography { FontFamily = Font, FontWeight = "600" },
            H6 = new H6Typography { FontFamily = Font, FontWeight = "600", FontSize = "1.1rem" },
        },
    };
}
