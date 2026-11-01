using System.Windows;

namespace LocalAiPerf.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Plot.Plot.Add.Scatter(new double[] { 1, 2, 3 }, new double[] { 1, 4, 9 });
        Plot.Refresh();
    }
}
