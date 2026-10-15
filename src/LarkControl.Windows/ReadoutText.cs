using System.Windows;
using System.Windows.Controls;

namespace LarkControl.Windows;

public static class ReadoutText
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(string), typeof(ReadoutText), new PropertyMetadata(string.Empty, OnValueChanged));

    public static string GetValue(DependencyObject element) => (string)element.GetValue(ValueProperty);

    public static void SetValue(DependencyObject element, string value) => element.SetValue(ValueProperty, value);

    private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (sender is TextBox readout)
            UiSupport.UpdateReadout(readout, eventArgs.NewValue as string ?? string.Empty);
    }
}
