using System.IO;
using System.Windows;
using System.Windows.Media.Media3D;
using AoiTwin.Rendering.Camera;
using AoiTwin.Rendering.Viewport;
using Xunit;

namespace AoiTwin.Rendering.Tests;

public sealed class FollowCameraTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-follow-" + Guid.NewGuid().ToString("N"));

    public FollowCameraTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Tracking_is_smooth_with_continuous_frame_steps()
    {
        RunOnStaThread(() =>
        {
            var viewport = new TwinViewport3D();
            var target = new Point3D(0, 0, 0.5);
            var follow = new FollowCamera(viewport, () => target);
            try
            {
                viewport.Camera.Position = new Point3D(1, -1, 1.5);
                viewport.Camera.LookDirection = target - viewport.Camera.Position;
                follow.Start();
                Assert.True(follow.IsFollowing);

                // 目标跳到远处：低通逐帧逼近——首帧位移恰为 α×距离（一阶低通响应），
                // 此后逐帧不增（连续无跳变）
                target = new Point3D(0.5, 0.8, 0.9);
                var previous = viewport.Camera.Position;
                // 平滑对象是目标点：首帧位移 = α × |新目标 − 原目标|（原目标 (0,0,0.5)）
                var initialDistance = (target - new Point3D(0, 0, 0.5)).Length;
                var maxStep = 0.0;
                var lastStep = double.MaxValue;
                var distanceToTarget = double.MaxValue;
                for (var frame = 0; frame < 30; frame++)
                {
                    follow.OnFrame();
                    var step = (viewport.Camera.Position - previous).Length;
                    if (frame == 0)
                    {
                        Assert.Equal(FollowCamera.SmoothingAlpha * initialDistance, step, 3);
                    }

                    Assert.True(step <= lastStep + 1e-9, $"第 {frame} 帧位移 {step:F4} 大于上一帧（非收敛）");
                    lastStep = step;
                    maxStep = Math.Max(maxStep, step);
                    previous = viewport.Camera.Position;

                    // 相机目标点 = Position + LookDirection（平滑目标），应逐帧逼近真实目标
                    var currentDistance =
                        (viewport.Camera.Position + viewport.Camera.LookDirection - target).Length;
                    Assert.True(currentDistance <= distanceToTarget + 1e-9, "目标距离应单调不增（低通收敛）");
                    distanceToTarget = currentDistance;
                }

                // 收敛后足够接近目标
                Assert.True(distanceToTarget < 0.02, $"30 帧后距目标 {distanceToTarget:F3} 未收敛");
                Assert.True(maxStep > 0); // 确实动过
            }
            finally
            {
                follow.Dispose();
            }
        });
    }

    [Fact]
    public void Stop_freezes_camera_and_null_target_keeps_last_view()
    {
        RunOnStaThread(() =>
        {
            var viewport = new TwinViewport3D();
            var target = new Point3D(0, 0, 0.5);
            Point3D? providerTarget = target;
            var follow = new FollowCamera(viewport, () => providerTarget);
            try
            {
                viewport.Camera.Position = new Point3D(1, -1, 1.5);
                viewport.Camera.LookDirection = target - viewport.Camera.Position;
                follow.Start();
                follow.OnFrame();
                // 目标未动：相机保持相对偏移（位置不变是正确行为）
                Assert.Equal(new Point3D(1, -1, 1.5), viewport.Camera.Position);

                // 目标移动：相机跟随平移（偏移恒定）——注意 provider 读的是 providerTarget
                providerTarget = new Point3D(0.9, 0.8, 0.9);
                for (var i = 0; i < 40; i++)
                {
                    follow.OnFrame();
                }

                var posWhileFollowing = viewport.Camera.Position;
                Assert.True(posWhileFollowing.X > 1.0, "相机应随目标 +X 平移");

                // 目标暂无数据（LOD 换档瞬间）：保持上一帧视角
                providerTarget = null;
                follow.OnFrame();
                Assert.Equal(posWhileFollowing, viewport.Camera.Position);

                // 解除：停在该处，后续目标变化不再影响相机
                providerTarget = target;
                follow.Stop();
                Assert.False(follow.IsFollowing);
                follow.OnFrame();
                Assert.Equal(posWhileFollowing, viewport.Camera.Position);
                providerTarget = new Point3D(9, 9, 9);
                follow.OnFrame();
                Assert.Equal(posWhileFollowing, viewport.Camera.Position);
            }
            finally
            {
                follow.Dispose();
            }
        });
    }

    [Fact]
    public void Start_without_target_is_noop()
    {
        RunOnStaThread(() =>
        {
            var viewport = new TwinViewport3D();
            Point3D? providerTarget = null;
            var follow = new FollowCamera(viewport, () => providerTarget);
            try
            {
                viewport.Camera.Position = new Point3D(1, -1, 1.5);
                follow.Start();
                Assert.False(follow.IsFollowing); // 无目标不切入
                follow.OnFrame();
                Assert.Equal(new Point3D(1, -1, 1.5), viewport.Camera.Position);
            }
            finally
            {
                follow.Dispose();
            }
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA 线程超时");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA 线程内断言失败: {failure}");
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
