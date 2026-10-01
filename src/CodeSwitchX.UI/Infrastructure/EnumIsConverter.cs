using System.Globalization;
using System.Windows.Data;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>True when the bound enum has the value named by the parameter. One way: a radio button shows the value, and
/// its click sets it through a command, so a click on the button already checked is not lost.</summary>
public sealed class EnumIsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
