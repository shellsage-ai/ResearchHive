using System.Windows;
using ResearchHive.ViewModels;

namespace ResearchHive;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
