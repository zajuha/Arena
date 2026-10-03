using Wpf.Ui.Controls;
using WinRepair.App.ViewModels;

namespace WinRepair.App.Views;

/// <summary>
/// Главное окно приложения. Вся логика находится в MainViewModel (MVVM, без логики в code-behind).
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
