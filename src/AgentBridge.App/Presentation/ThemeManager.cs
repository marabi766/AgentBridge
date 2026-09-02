using Media = System.Windows.Media;

namespace AgentBridge.App;

public static class ThemeManager
{
    private static readonly IReadOnlyDictionary<string, (Media.Color Light, Media.Color Dark)> Colors =
        new Dictionary<string, (Media.Color, Media.Color)>
        {
            ["WindowBrush"] = (Parse("#F3F3F1"), Parse("#1C1C1B")),
            ["PanelBrush"] = (Parse("#FFFFFF"), Parse("#262625")),
            ["SubtleBrush"] = (Parse("#FAF9F7"), Parse("#2E2E2C")),
            ["StrokeBrush"] = (Parse("#D8D8D6"), Parse("#3A3A38")),
            ["TextBrush"] = (Parse("#1B1B19"), Parse("#F2F2F0")),
            ["SecondaryTextBrush"] = (Parse("#5C5C58"), Parse("#ADACA8")),
            ["AccentBrush"] = (Parse("#005FB8"), Parse("#6CB8F6")),
            ["WarningBrush"] = (Parse("#8A5A00"), Parse("#F2C14E")),
            ["ErrorBrush"] = (Parse("#B42318"), Parse("#FF8A80")),
            ["VerifiedBrush"] = (Parse("#157347"), Parse("#6DD5A0")),
            ["NavigationBrush"] = (Parse("#ECECE9"), Parse("#20201F")),
            ["ModeBrush"] = (Parse("#374151"), Parse("#4B5563")),
            ["WarningSurfaceBrush"] = (Parse("#FFF8E7"), Parse("#3A3020")),
            ["ErrorSurfaceBrush"] = (Parse("#FFF1F0"), Parse("#3B2221")),
            ["AccentTextBrush"] = (Parse("#FFFFFF"), Parse("#101820")),
        };

    public static void Apply(bool dark)
    {
        foreach (var (key, pair) in Colors)
        {
            System.Windows.Application.Current.Resources[key] =
                new Media.SolidColorBrush(dark ? pair.Dark : pair.Light);
        }
    }

    private static Media.Color Parse(string value) => (Media.Color)Media.ColorConverter.ConvertFromString(value);
}
