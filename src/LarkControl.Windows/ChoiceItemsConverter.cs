using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace LarkControl.Windows;

public sealed class ChoiceItemsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IReadOnlyList<string> captions
            ? captions.Select((caption, index) => new GuardedSettingComboBoxItem { Content = caption, Tag = index }).ToArray()
            : Array.Empty<ComboBoxItem>();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
