using System.Windows;
using AoiTwin.App.ViewModels;

namespace AoiTwin.App;

/// <summary>主窗口：仅做视图模型注入，页面内容全部由 XAML 绑定驱动。</summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
