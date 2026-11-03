using System.IO;
using System.Windows.Media.Media3D;
using AoiTwin.Rendering.Camera;
using Xunit;

namespace AoiTwin.Rendering.Tests;

/// <summary>
/// T1.5 相机书签：过渡时长判据（FR-M1-06 ≤ 800ms）、球面插值正确性、书签持久化往返。
/// </summary>
public sealed class CameraBookmarkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aoi-bookmarks-" + Guid.NewGuid().ToString("N"));

    private static CameraPose Pose(double x, double y, double z, double tx = 0, double ty = 0, double tz = 0.7, double fov = 45)
        => new(new Point3D(x, y, z), new Point3D(tx, ty, tz), fov);

    [Fact]
    public void Transition_duration_never_exceeds_acceptance_budget()
    {
        // 极端变化：反向 180° + 距离 10 倍 + 目标点横移 5m
        var extremes = new[]
        {
            CameraTransition.Create(Pose(3.1, -3.8, 2.5), Pose(-3.1, 3.8, -2.5)),
            CameraTransition.Create(Pose(0.2, -0.2, 0.4), Pose(25, -25, 18)),
            CameraTransition.Create(Pose(3.1, -3.8, 2.5), Pose(3.1, -3.8, 2.5, 5, 5, 5)),
            CameraTransition.Create(Pose(3.1, -3.8, 2.5), CameraPose.DefaultFieldOfView == 45 ? Pose(3.1, -3.8, 2.5) : default),
        };

        Assert.All(extremes, t => Assert.True(
            t.Duration.TotalMilliseconds <= 800,
            $"过渡 {t} 超出 800ms 判据"));

        // 大跨度的时长应贴近上限（证明时长是按变化量自适应，而不是恒定值）
        Assert.True(extremes[0].Duration.TotalMilliseconds > 700);
    }

    [Fact]
    public void Transition_of_identical_pose_is_shortest_but_not_zero()
    {
        var pose = Pose(3.1, -3.8, 2.5);
        var transition = CameraTransition.Create(pose, pose);

        Assert.Equal(CameraTransition.MinDurationMs, transition.Duration.TotalMilliseconds, 1);
        Assert.Equal(0, pose.ChangeRatio(pose), 6);
    }

    [Fact]
    public void Interpolation_hits_endpoints_exactly()
    {
        var from = Pose(3.1, -3.8, 2.5);
        var to = Pose(0.95, -1.35, 1.45, 0, -0.32, 1.0, 38);
        var transition = CameraTransition.Create(from, to);

        Assert.Equal(from, transition.PoseAt(0));
        Assert.Equal(to, transition.PoseAt(1));

        // 越界进度也钳制到端点（掉帧/超时不会把相机甩出去）
        Assert.Equal(from, transition.PoseAt(-0.5));
        Assert.Equal(to, transition.PoseAt(2.4));
    }

    [Fact]
    public void Interpolation_orbits_target_instead_of_cutting_through_it()
    {
        // 从 +X 侧绕到 -X 侧：直线插值会穿过设备中心，球面插值必须始终与目标点保持正距离
        var from = Pose(4, 0, 0.7);
        var to = Pose(-4, 0, 0.7);

        var minDistance = double.MaxValue;
        for (var i = 1; i < 100; i++)
        {
            var pose = from.Interpolate(to, i / 100.0);
            minDistance = Math.Min(minDistance, pose.Distance);
        }

        Assert.Equal(4, minDistance, 3); // 半径不变
    }

    [Fact]
    public void Interpolation_takes_shortest_arc_across_the_seam()
    {
        // 170° → -170°：最短弧应经过 180°（Y 保持接近 0），而不是绕回 0°（Y 变成大幅正值）
        const double r = 4;
        var start = 170 * Math.PI / 180;
        var end = -170 * Math.PI / 180;

        var from = new CameraPose(
            new Point3D(r * Math.Cos(start), r * Math.Sin(start), 0.7), new Point3D(0, 0, 0.7));
        var to = new CameraPose(
            new Point3D(r * Math.Cos(end), r * Math.Sin(end), 0.7), new Point3D(0, 0, 0.7));

        var mid = from.Interpolate(to, 0.5);

        Assert.True(Math.Abs(mid.Position.Y) < 0.5, $"中点 Y={mid.Position.Y:F2}，说明走了长弧");
        Assert.True(mid.Position.X < 0, "中点应绕到 -X 侧（180° 附近）");
    }

    [Fact]
    public void Field_of_view_is_interpolated()
    {
        var from = Pose(3, -3, 2.5, fov: 45);
        var to = Pose(3, -3, 2.5, fov: 25);

        Assert.Equal(35, from.Interpolate(to, 0.5).FieldOfView, 6);
    }

    [Fact]
    public void Pose_round_trips_through_camera()
    {
        var pose = Pose(1.2, -2.4, 1.8, 0.1, 0.2, 0.9, 38);
        var camera = pose.ToCamera();

        var back = CameraPose.FromCamera(camera);
        Assert.Equal(pose.Position.X, back.Position.X, 9);
        Assert.Equal(pose.Target.Z, back.Target.Z, 9);
        Assert.Equal(pose.FieldOfView, back.FieldOfView, 9);

        // ApplyTo 必须原地改实例（Helix 视口依赖属性变更通知）
        var other = pose.ToCamera();
        Pose(9, 9, 9).ApplyTo(other);
        Assert.Equal(9, other.Position.X, 6);
        Assert.Same(other, other);
    }

    [Fact]
    public void Default_pose_matches_first_preset()
    {
        var first = CameraBookmarkService.Presets[0];
        Assert.Equal(4, CameraBookmarkService.Presets.Count);
        Assert.Equal("整机等轴测", first.Name);
        Assert.Equal(first.Pose.Position.X, Viewport.TwinViewport3D.DefaultPose.Position.X, 6);
    }

    [Fact]
    public void Bookmarks_round_trip_through_disk()
    {
        var service = new CameraBookmarkService(Path.Combine(_dir, "bookmarks.json"));
        Assert.False(service.Load()); // 首次无文件

        var saved = service.Save("  我的视角  ", Pose(1, -1, 1.5, 0, 0, 1));
        Assert.Equal("我的视角", saved.Name); // 名称去空白
        Assert.Equal(CameraBookmarkKind.Custom, saved.Kind);
        Assert.True(File.Exists(service.StorePath));
        Assert.False(File.Exists(service.StorePath + ".tmp")); // 不留临时文件

        var reloaded = new CameraBookmarkService(service.StorePath);
        Assert.True(reloaded.Load());
        Assert.Single(reloaded.Custom);
        Assert.Equal("我的视角", reloaded.Custom[0].Name);
        Assert.Equal(1, reloaded.Custom[0].Pose.Position.X, 6);
        Assert.Equal(4 + 1, reloaded.All.Count); // 预置 + 自定义
        Assert.Contains(reloaded.All, b => b.IsPreset);
    }

    [Fact]
    public void Saving_same_name_overwrites_and_delete_removes()
    {
        var service = new CameraBookmarkService(Path.Combine(_dir, "bookmarks.json"));
        service.Save("A", Pose(1, -1, 1));
        service.Save("A", Pose(2, -2, 2));

        Assert.Single(service.Custom);
        Assert.Equal(2, service.Custom[0].Pose.Position.X, 6);

        Assert.True(service.Delete("A"));
        Assert.Empty(service.Custom);
        Assert.False(service.Delete("A"));

        var reloaded = new CameraBookmarkService(service.StorePath);
        Assert.False(reloaded.Load());
        Assert.Empty(reloaded.Custom);
    }

    [Fact]
    public void Preset_name_collision_and_invalid_pose_are_rejected()
    {
        var service = new CameraBookmarkService(Path.Combine(_dir, "bookmarks.json"));

        Assert.Throws<ArgumentException>(() => service.Save("正面总览", Pose(1, -1, 1)));
        Assert.Throws<ArgumentException>(() => service.Save("  ", Pose(1, -1, 1)));

        // 位置与目标重合 → 非法位姿
        var degenerate = new CameraPose(new Point3D(1, 1, 1), new Point3D(1, 1, 1));
        Assert.False(degenerate.IsValid);
        Assert.Throws<ArgumentException>(() => service.Save("重合", degenerate));
    }

    [Fact]
    public void Custom_bookmarks_are_capped_and_evict_oldest()
    {
        var service = new CameraBookmarkService(Path.Combine(_dir, "bookmarks.json"));
        for (var i = 0; i < CameraBookmarkService.MaxCustomBookmarks + 3; i++)
        {
            service.Save($"V{i}", Pose(1 + i, -1, 1));
        }

        Assert.Equal(CameraBookmarkService.MaxCustomBookmarks, service.Custom.Count);
        Assert.Null(service.Find("V0"));
        Assert.NotNull(service.Find($"V{CameraBookmarkService.MaxCustomBookmarks + 2}"));
    }

    [Fact]
    public void Corrupt_store_degrades_to_empty_without_throwing()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "broken.json");
        File.WriteAllText(path, "{ this is not json");

        var service = new CameraBookmarkService(path);
        Assert.False(service.Load());
        Assert.Empty(service.Custom);

        // 单条损坏只丢该条，其余仍可用
        File.WriteAllText(path, """
            {
              "version": 1,
              "custom": [
                { "name": "坏条目", "px": 0, "py": 0, "pz": 0, "tx": 0, "ty": 0, "tz": 0, "fov": 45 },
                { "name": "好条目", "px": 2, "py": -2, "pz": 2, "tx": 0, "ty": 0, "tz": 1, "fov": 45 }
              ]
            }
            """);

        Assert.True(service.Load());
        Assert.Single(service.Custom);
        Assert.Equal("好条目", service.Custom[0].Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
