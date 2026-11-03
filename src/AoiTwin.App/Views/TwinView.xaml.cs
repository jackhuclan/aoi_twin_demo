using System.Windows;
using System.Windows.Controls;

namespace AoiTwin.App.Views;

/// <summary>
/// 3D 场景页（T1.3）：视口交互属视图行为（等同 ScrollViewer 的滚动语义），
/// 直接操作 <c>TwinViewport3D</c>；场景内容与书签一旦接入（T1.4/T1.5）再引入视图模型承载状态。
/// </summary>
public partial class TwinView : UserControl
{
    public TwinView() => InitializeComponent();

    /// <summary>视口实例（测试与后续 T1.4/T1.5 场景挂载需要编程访问）。</summary>
    public Rendering.Viewport.TwinViewport3D TwinViewport => Viewport;

    private void OnResetViewClick(object sender, RoutedEventArgs e) => Viewport.ResetView();

    private void OnToggleProbeClick(object sender, RoutedEventArgs e)
    {
        Viewport.IsFrameProbeVisible = !Viewport.IsFrameProbeVisible;
        ProbeToggleButton.Content = Viewport.IsFrameProbeVisible ? "隐藏帧探针" : "显示帧探针";
    }
}
