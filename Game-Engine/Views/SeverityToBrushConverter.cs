using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using Game_Engine.Core;

namespace Game_Engine.Views
{
    /// <summary>
    /// Maps LogSeverity -> theme-aware Brush. Kind picks the palette (chip background, border, or message text).
    /// </summary>
    public sealed class SeverityToBrushConverter : IValueConverter
    {
        public string Kind { get; set; } = "Text"; // "Background" | "Border" | "Text"

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var sev = value is LogSeverity s ? s : LogSeverity.Info;
            var isLight = Application.Current?.ActualThemeVariant == ThemeVariant.Light;

            // Badge background & border are saturated so white badge text meets WCAG AA in both themes.
            // Message text adapts to Light vs Dark theme for high contrast.
            (Color bg, Color border, Color text) palette = sev switch
            {
                LogSeverity.Info => (
                    Color.Parse("#1E64B8"),
                    Color.Parse("#3B82F6"),
                    isLight ? Color.Parse("#1E293B") : Color.Parse("#E2E8F0")),
                LogSeverity.Warning => (
                    Color.Parse("#B45309"),
                    Color.Parse("#D97706"),
                    isLight ? Color.Parse("#92400E") : Color.Parse("#FCD34D")),
                LogSeverity.Error => (
                    Color.Parse("#C81E1E"),
                    Color.Parse("#EF4444"),
                    isLight ? Color.Parse("#B91C1C") : Color.Parse("#FCA5A5")),
                LogSeverity.Success => (
                    Color.Parse("#15803D"),
                    Color.Parse("#22C55E"),
                    isLight ? Color.Parse("#166534") : Color.Parse("#86EFAC")),
                LogSeverity.Debug => (
                    Color.Parse("#4B5563"),
                    Color.Parse("#6B7280"),
                    isLight ? Color.Parse("#475569") : Color.Parse("#CBD5E1")),
                _ => (
                    Color.Parse("#4B5563"),
                    Color.Parse("#6B7280"),
                    isLight ? Color.Parse("#1E293B") : Color.Parse("#E2E8F0"))
            };

            return Kind switch
            {
                "Background" => new SolidColorBrush(palette.bg),
                "Border" => new SolidColorBrush(palette.border),
                _ => new SolidColorBrush(palette.text),
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null!;
    }
}
