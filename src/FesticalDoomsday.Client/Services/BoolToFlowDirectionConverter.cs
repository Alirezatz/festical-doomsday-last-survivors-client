using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FesticalDoomsday.Client.Services;

/// <summary>
/// Turns the "is this text right to left" flag of a console line into the
/// direction the text has to be laid out in. Without it a Persian line inside
/// the left to right console would end up with its punctuation on the wrong
/// side.
/// </summary>
public sealed class BoolToFlowDirectionConverter : IValueConverter
{
    public static BoolToFlowDirectionConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FlowDirection.RightToLeft;
}
