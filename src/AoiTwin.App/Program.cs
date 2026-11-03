using System.Windows;

namespace AoiTwin.App;

/// <summary>
/// T0 骨架占位入口：仅验证 WinExe 可启动。
/// T1.3 起替换为 Host 构建 + DI 装配 + 3D 视口主窗（App.xaml.cs 只做装配，禁止业务逻辑）。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var window = new Window { Title = "AoiTwin", Width = 1280, Height = 800 };
        new Application { MainWindow = window }.Run(window);
    }
}
