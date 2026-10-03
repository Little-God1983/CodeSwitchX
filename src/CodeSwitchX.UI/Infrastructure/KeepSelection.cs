using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// A list that is a choice, one item always picked (an engine, a voice, the effort): Ctrl+click or Ctrl+Space on the
/// item picked would unpick it, leave none picked and write null to the setting bound. Those are ignored. Only the
/// user's input is: a value set in code, or an item gone from the list, changes the selection as before.
/// </summary>
public static class KeepSelection
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(KeepSelection), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list)
        {
            return;
        }

        list.PreviewMouseLeftButtonDown -= OnMouseDown;
        list.PreviewKeyDown -= OnKeyDown;
        if (e.NewValue is true)
        {
            list.PreviewMouseLeftButtonDown += OnMouseDown;
            list.PreviewKeyDown += OnKeyDown;
        }
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && ItemOf(e.OriginalSource as DependencyObject, sender) is { IsSelected: true })
        {
            e.Handled = true;
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && ItemOf(e.OriginalSource as DependencyObject, sender) is { IsSelected: true })
        {
            e.Handled = true;
        }
    }

    /// <summary>The item of <paramref name="list"/> holding <paramref name="element"/>, not one of a list inside it.</summary>
    private static ListBoxItem? ItemOf(DependencyObject? element, object list)
    {
        for (var node = element; node is not null && node != list; node = node is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(node)
                 : LogicalTreeHelper.GetParent(node))
        {
            if (node is ListBoxItem item)
            {
                return ItemsControl.ItemsControlFromItemContainer(item) == list ? item : null;
            }
        }

        return null;
    }
}
