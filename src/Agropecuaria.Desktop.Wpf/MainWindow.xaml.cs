using System.Windows;

namespace Agropecuaria.Desktop.Wpf;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    public MainWindow(InventoryViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.PollAsync(lifetime.Token);
        Closed += (_, _) => lifetime.Cancel();
    }
}
