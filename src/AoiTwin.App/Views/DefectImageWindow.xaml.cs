using System.Windows;
using AoiTwin.App.ViewModels;

namespace AoiTwin.App.Views;

/// <summary>
/// 缺陷原图窗口（TA.14）：3D 点选 → 反查 DefectId → 2D 列表定位后由视图打开。
/// 无状态、可重入——同一缺陷重复点选只是重新赋值 DataContext。
/// </summary>
public partial class DefectImageWindow : Window
{
    public DefectImageWindow(DefectImageViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        Title = $"缺陷原图 · #{viewModel.DefectId}";
    }

    /// <summary>窗口展示的缺陷（测试断言用）。</summary>
    public DefectImageViewModel ViewModel => (DefectImageViewModel)DataContext;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
