using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Game_Engine.Core;

namespace Game_Engine.Views
{
    /// <summary>
    /// Maps LogSeverity -> Brush. Kind picks the palette (chip background, border, or text).
    /// </summary>
    public sealed class SeverityToBrushConverter : IValueConverter
    {
        public string Kind { get; set; } = "Text"; // "Background" | "Border" | "Text"

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var sev = value is LogSeverity s ? s : LogSeverity.Info;

            // Colors matched to "chip" look
            (Color bg, Color border, Color text) palette = sev switch
            {
                LogSeverity.Info => (Color.Parse("#223b6b"), Color.Parse("#8ab4f8"), Color.Parse("#cfe3ff")),
                LogSeverity.Warning => (Color.Parse("#3f3310"), Color.Parse("#fbbc04"), Color.Parse("#ffd87a")),
                LogSeverity.Error => (Color.Parse("#3f1e1c"), Color.Parse("#ea4335"), Color.Parse("#ff9a92")),
                LogSeverity.Success => (Color.Parse("#203824"), Color.Parse("#34a853"), Color.Parse("#9be2b0")),
                LogSeverity.Debug => (Color.Parse("#2b2140"), Color.Parse("#c58af9"), Color.Parse("#e2c7ff")),
                _ => (Colors.Black, Colors.Gray, Colors.White)
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
