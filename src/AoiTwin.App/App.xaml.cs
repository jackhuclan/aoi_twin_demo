using System.Windows;
using AoiTwin.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AoiTwin.App;

/// <summary>
/// 应用入口（T3.2）：只做 Host 构建与 DI 装配，禁止业务逻辑。
/// 视图模型全部单例——回放会话、录制目录状态在标签切换间必须保持。
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder();
        // 注册表见 AppServices（与 DiCompositionTests 共用同一份，避免「注册与实际构造不一致」）
        builder.Services.AddAoiTwinApp();

        _host = builder.Build();
        _host.Start();

        MainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 视图模型持有回放会话（文件句柄 + 泵线程），退出前必须释放
        _host?.Services.GetService<MainViewModel>()?.Dispose();
        _host?.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        _host?.Dispose();
        base.OnExit(e);
    }
}
