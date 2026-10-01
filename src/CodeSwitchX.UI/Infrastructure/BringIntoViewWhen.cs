using System.Windows;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>Scrolls an element into view whenever the bound value turns true: a view model cannot reach the scroll viewer.</summary>
public static class BringIntoViewWhen
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(bool), typeof(BringIntoViewWhen), new PropertyMetadata(false, OnValueChanged));

    public static bool GetValue(DependencyObject element) => (bool)element.GetValue(ValueProperty);

    public static void SetValue(DependencyObject element, bool value) => element.SetValue(ValueProperty, value);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && d is FrameworkElement element)
        {
            // After the shell has switched to the Yard and laid it out: before, the element has no place to scroll to.
            element.Dispatcher.BeginInvoke(() => element.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}
