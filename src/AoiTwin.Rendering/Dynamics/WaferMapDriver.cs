using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AoiTwin.Core.Bus;
using AoiTwin.Rendering.Scene;
using AoiTwin.Rendering.Viewport;
using AoiTwin.ViewModel;

namespace AoiTwin.Rendering.Dynamics;

/// <summary>
/// Wafer Map 纹理驱动（TA.7，FR-M2-06）：
/// - 晶圆圆片平面网格**一次建好**（随装配重绑），材质 <see cref="ImageBrush"/> + <see cref="WriteableBitmap"/>；
/// - <see cref="TwinVisualViewModel.WaferQueue"/> 的整片事件只走 <see cref="WriteableBitmap.WritePixels"/>——
///   几何与位姿引用零改动（GPU 纹理替换，NFR-11 整片刷新掉帧 &lt; 5fps 的结构性前提）；
/// - DieMap 字节约定（M1 契约的显式化，二值/灰度通吃）：
///   0 = 未测（深灰底）、1 = OK（绿）、2 = NG（红）、3..255 = OK→NG 灰度渐变。
/// </summary>
public sealed class WaferMapDriver : IDisposable
{
    /// <summary>承载晶圆的部件（PRD §5.6 台账约定，PartId 固定）。</summary>
    public const string WaferPartId = "WAFER_CURRENT";

    /// <summary>未测 die 底色。</summary>
    private static readonly Color UntestedColor = Color.FromRgb(0x1E, 0x2A, 0x32);
    private static readonly Color OkColor = Color.FromRgb(0x2E, 0xD5, 0x73);
    private static readonly Color NgColor = Color.FromRgb(0xFF, 0x47, 0x57);

    private readonly TwinViewport3D _viewport;
    private readonly TwinVisualViewModel _visualViewModel;
    private readonly Func<SceneAssembly?> _assemblyProvider;
    private readonly ModelVisual3D _visualRoot = new();
    private WriteableBitmap? _bitmap;
    private int _mapSizeX;
    private int _mapSizeY;
    private SceneAssembly? _boundAssembly;
    private bool _disposed;

    public WaferMapDriver(TwinViewport3D viewport, TwinVisualViewModel visualViewModel, Func<SceneAssembly?> assemblyProvider)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(visualViewModel);
        ArgumentNullException.ThrowIfNull(assemblyProvider);

        _viewport = viewport;
        _visualViewModel = visualViewModel;
        _assemblyProvider = assemblyProvider;
        viewport.FrameRendered += OnViewportFrameRendered;
    }

    /// <summary>当前纹理尺寸（die 数；无数据时 0，诊断用）。</summary>
    public (int X, int Y) TextureSize => (_mapSizeX, _mapSizeY);

    /// <summary>累计整片刷新次数（诊断用）。</summary>
    public long RefreshCount { get; private set; }

    private void OnViewportFrameRendered(object? sender, TimeSpan e) => OnFrame();

    /// <summary>单帧驱动（公开供无头测试直接调用）。</summary>
    public void OnFrame()
    {
        if (_disposed)
        {
            return;
        }

        var assembly = _assemblyProvider();
        if (!ReferenceEquals(assembly, _boundAssembly))
        {
            Rebind(assembly);
        }

        // 上传队列：满片事件逐个消费（不丢不重由队列保证），每片一次 WritePixels
        while (_visualViewModel.WaferQueue.TryTake(out var result))
        {
            ApplyMap(result.Map);
        }
    }

    private void Rebind(SceneAssembly? assembly)
    {
        _boundAssembly = assembly;
        _visualRoot.Children.Clear();
        _bitmap = null;
        _mapSizeX = 0;
        _mapSizeY = 0;

        if (assembly?.Find(WaferPartId) is not { } wafer)
        {
            _viewport.OverlayRoot.Children.Remove(_visualRoot);
            return;
        }

        var b = wafer.Geometry.Bounds;
        var radius = Math.Min(b.SizeX, b.SizeY) / 2;
        var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ + 0.0008);

        var brush = new ImageBrush(_bitmap = CreateBitmap(1, 1)) // 占位 1×1，首片数据时重建到实际尺寸
        {
            ViewportUnits = BrushMappingMode.Absolute,
            TileMode = TileMode.None,
        };
        var material = new DiffuseMaterial(brush); // 未冻结：贴图随帧更新

        var disc = new GeometryModel3D
        {
            Geometry = CreateDiscGeometry(radius, center.Z),
            Material = material,
            Transform = new TranslateTransform3D(center.X, center.Y, 0),
        };
        var visual = new ModelVisual3D { Content = disc };
        _visualRoot.Children.Add(visual);

        if (_viewport.OverlayRoot.Children.IndexOf(_visualRoot) < 0)
        {
            _viewport.OverlayRoot.Children.Add(_visualRoot);
        }
    }

    private void ApplyMap(WaferMapSnapshot map)
    {
        if (_bitmap is null)
        {
            return; // 装配未就绪：队列已消费，片数据不回灌（下片数据为最新态）
        }

        // 尺寸变化（换产品配方）→ 重建纹理缓冲；尺寸不变 → 原地 WritePixels
        if (_mapSizeX != map.SizeX || _mapSizeY != map.SizeY)
        {
            _bitmap = CreateBitmap(map.SizeX, map.SizeY);
            _mapSizeX = map.SizeX;
            _mapSizeY = map.SizeY;
            if (_visualRoot.Children.Count > 0
                && ((GeometryModel3D)((ModelVisual3D)_visualRoot.Children[0]).Content).Material
                    is DiffuseMaterial { Brush: ImageBrush brush })
            {
                brush.ImageSource = _bitmap;
            }
        }

        var pixels = new byte[map.SizeX * map.SizeY * 4];
        var dieMap = map.DieMap;
        var expected = map.SizeX * map.SizeY;
        for (var i = 0; i < expected && i < dieMap.Length; i++)
        {
            var color = DieColor(dieMap[i]);
            var o = i * 4; // Bgra32
            pixels[o] = color.B;
            pixels[o + 1] = color.G;
            pixels[o + 2] = color.R;
            pixels[o + 3] = 0xFF;
        }

        _bitmap.WritePixels(
            new Int32Rect(0, 0, _mapSizeX, _mapSizeY),
            pixels,
            _mapSizeX * 4,
            0);
        RefreshCount++;
    }

    /// <summary>DieMap 字节 → 颜色（见类注释约定）。</summary>
    private static Color DieColor(byte value) => value switch
    {
        0 => UntestedColor,
        1 => OkColor,
        2 => NgColor,
        _ => Lerp(OkColor, NgColor, (value - 3) / 252.0), // 3..255 灰度渐变
    };

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + ((b.R - a.R) * t)),
        (byte)(a.G + ((b.G - a.G) * t)),
        (byte)(a.B + ((b.B - a.B) * t)));

    private static WriteableBitmap CreateBitmap(int width, int height) => new(
        width, height, 96, 96, PixelFormats.Bgra32, null);

    /// <summary>晶圆圆盘网格（32 段，UV 中心 0.5/0.5；一次建好，驱动器不改几何）。</summary>
    private static MeshGeometry3D CreateDiscGeometry(double radius, double z)
    {
        const int segments = 32;
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection(segments + 2);
        var uvs = new PointCollection(segments + 2);
        var indices = new Int32Collection(segments * 3);

        positions.Add(new Point3D(0, 0, z)); // 圆心
        uvs.Add(new Point(0.5, 0.5));
        for (var i = 0; i <= segments; i++)
        {
            var angle = 2 * Math.PI * i / segments;
            positions.Add(new Point3D(radius * Math.Cos(angle), radius * Math.Sin(angle), z));
            uvs.Add(new Point(0.5 + 0.5 * Math.Cos(angle), 0.5 + 0.5 * Math.Sin(angle)));
        }

        for (var i = 1; i <= segments; i++)
        {
            indices.Add(0);
            indices.Add(i + 1);
            indices.Add(i);
        }

        mesh.Positions = positions;
        mesh.TextureCoordinates = uvs;
        mesh.TriangleIndices = indices;
        mesh.Freeze();
        return mesh;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewport.FrameRendered -= OnViewportFrameRendered;
        _visualRoot.Children.Clear();
        _viewport.OverlayRoot.Children.Remove(_visualRoot);
    }
}
