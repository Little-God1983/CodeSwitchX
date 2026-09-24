using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodeSwitchX.UI.Telemetry;

public partial class PerformanceBarView : UserControl
{
    public PerformanceBarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Sparkline.SizeChanged += (_, _) => Redraw();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PerformanceBarViewModel old)
        {
            old.PropertyChanged -= OnViewModelChanged;
        }

        if (e.NewValue is PerformanceBarViewModel vm)
        {
            vm.PropertyChanged += OnViewModelChanged;
            Redraw();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PerformanceBarViewModel.RateNormalized) or null)
        {
            Redraw();
        }
    }

    private void Redraw()
    {
        if (DataContext is not PerformanceBarViewModel vm || vm.RateNormalized.Length < 2 || Sparkline.ActualWidth <= 0)
        {
            return;
        }

        var points = new PointCollection();
        var width = Sparkline.ActualWidth;
        var height = Sparkline.ActualHeight;
        var step = width / (vm.RateNormalized.Length - 1);
        for (var i = 0; i < vm.RateNormalized.Length; i++)
        {
            points.Add(new Point(i * step, height - vm.RateNormalized[i] * (height - 2) - 1));
        }

        SparklinePath.Points = points;
    }
}
