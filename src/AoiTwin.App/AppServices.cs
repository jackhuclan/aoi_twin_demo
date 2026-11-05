using System.IO;
using AoiTwin.App.Services;
using AoiTwin.App.ViewModels;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Kinematics;
using AoiTwin.ViewModel;
using Microsoft.Extensions.DependencyInjection;

namespace AoiTwin.App;

/// <summary>
/// 应用服务注册（单一事实来源）。
///
/// 为什么独立成方法：<see cref="App"/> 与集成测试必须用**同一份**注册表——
/// M2 曾经出现过「DI 注册了位姿引擎，但 <c>TwinSceneViewModel</c> 走了无参构造自建了一套 hub + 引擎」
/// 的假接线：界面能开、单测全绿，真机却收不到任何运动数据。把注册抽出来、并用
/// <c>DiCompositionTests</c> 断言「场景 VM 拿到的 hub 就是容器里的 hub」，这类问题才能被 CI 拦住。
/// </summary>
public static class AppServices
{
    /// <summary>注册全部应用服务（顺序无关，依赖由容器解析）。</summary>
    /// <param name="analyticsOptions">
    /// 分析运行时参数覆盖；null 时用 <c>%LOCALAPPDATA%\AoiTwin</c> 下的默认目录。
    /// 测试与离线工具用它把数据写到临时目录，避免污染用户真实库。
    /// </param>
    public static IServiceCollection AddAoiTwinApp(
        this IServiceCollection services,
        AnalyticsRuntimeOptions? analyticsOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var assetsRoot = AssetPackageLocator.LocateAssetsRoot();

        services.AddSingleton<IRealtimeHub, RealtimeHub>();

        // 运动链路（TA.4）：示教标定缺文件时恒等降级 → 位姿引擎 → 视觉 VM → 场景 VM
        services.AddSingleton(_ => new MotionPoseEngine(
            TeachCoordinateMapper.Load(Path.Combine(assetsRoot, "teach-calibration.json"))));
        // 引擎以接口暴露：TwinVisualViewModel 构造参数是 IMotionPoseSource，
        // 只注册具体类型会让 DI 用「可选参数默认 null」静默跳过——运动链路直接断掉
        services.AddSingleton<IMotionPoseSource>(sp => sp.GetRequiredService<MotionPoseEngine>());

        // 配色（TA.5 / PRD §5.5，热加载；文件缺失回落默认三表）
        services.AddSingleton(_ => new LightChannelPaletteFileSource(
            Path.Combine(assetsRoot, "light-channel-colors.json")));

        // 节流策略（TA.1 / FR-M2-01，热加载；文件缺失回落 DefaultMotionPolicy）
        services.AddSingleton(_ => new ThrottlePolicyFileSource(
            Path.Combine(assetsRoot, "throttle-policy.json")));

        services.AddSingleton(sp => new TwinVisualViewModel(
            sp.GetRequiredService<IRealtimeHub>(),
            sp.GetRequiredService<IMotionPoseSource>(),
            sp.GetRequiredService<ThrottlePolicyFileSource>().PolicyOf("Motion")));

        services.AddSingleton<TwinSceneViewModel>();
        services.AddSingleton<CollectionMonitorViewModel>();
        services.AddSingleton<ReplayViewModel>();
        services.AddSingleton<RecordingManagerViewModel>();

        // M3 分析链路（TB.0–TB.16）：时序库 + Sink + 档案聚合 + 预测器 + 调度 + 导出
        services.AddAoiTwinAnalytics(assetsRoot, analyticsOptions);

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }

    /// <summary>
    /// 注册 M3 分析链路（与 <see cref="AddAoiTwinApp"/> 分开，便于离线工具/测试只装分析半边）。
    ///
    /// 数据落在 <c>%LOCALAPPDATA%\AoiTwin\timeseries</c>，导出落在 <c>…\exports</c>——
    /// 与交付目录 <c>assets/recordings</c> 刻意分离：演示与联调产生的数据不得污染现场班次清单。
    /// </summary>
    public static IServiceCollection AddAoiTwinAnalytics(
        this IServiceCollection services,
        string assetsRoot,
        AnalyticsRuntimeOptions? analyticsOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.GetTempPath();
        }

        var runtimeOptions = analyticsOptions ?? new AnalyticsRuntimeOptions(
            StoreRoot: Path.Combine(local, "AoiTwin", "timeseries"),
            ExportDirectory: Path.Combine(local, "AoiTwin", "exports"),
            OptionsFilePath: Path.Combine(assetsRoot, "analytics-options.json"));

        services.AddSingleton(runtimeOptions);
        // 运行时同时持有 Sink（写）与调度器（读）：单例，随容器释放
        services.AddSingleton(sp => new AnalyticsRuntime(
            sp.GetRequiredService<IRealtimeHub>(),
            sp.GetRequiredService<AnalyticsRuntimeOptions>()));
        services.AddSingleton<HealthAnalyticsViewModel>();

        return services;
    }
}
