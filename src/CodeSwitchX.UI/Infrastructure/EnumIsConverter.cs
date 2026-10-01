using System.Globalization;
using System.Windows.Data;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>True when the bound enum has the value named by the parameter; checking the radio button sets that value.</summary>
public sealed class EnumIsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, (string)parameter) : Binding.DoNothing;
}
