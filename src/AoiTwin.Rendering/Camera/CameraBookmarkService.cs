using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Media3D;

namespace AoiTwin.Rendering.Camera;

/// <summary>
/// 视角书签服务（T1.5）：四个预置视角 + 自定义书签的增删与 JSON 持久化。
///
/// 持久化位置默认 <c>%LOCALAPPDATA%\AoiTwin\camera-bookmarks.json</c>（与资产包、录制目录分离，
/// 现场机器重装资产不影响操作员习惯）；写入走「临时文件 + 原子替换」，断电不会留下半个文件。
/// 文件损坏/字段缺失一律降级为空列表，不阻断应用启动。
/// </summary>
public sealed class CameraBookmarkService
{
    /// <summary>默认文件名。</summary>
    public const string DefaultFileName = "camera-bookmarks.json";

    /// <summary>自定义书签上限（超出后淘汰最早保存的一条）。</summary>
    public const int MaxCustomBookmarks = 16;

    /// <summary>当前存储格式版本（未来加字段时用于迁移）。</summary>
    public const int StoreVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly List<CameraBookmark> _custom = [];

    public CameraBookmarkService(string? storePath = null)
        => StorePath = storePath ?? DefaultStorePath();

    /// <summary>四个预置视角：整机等轴测 / 正面总览 / 顶部俯视 / 检测头特写。</summary>
    public static IReadOnlyList<CameraBookmark> Presets { get; } =
    [
        new("整机等轴测", new CameraPose(new Point3D(3.1, -3.8, 2.5), new Point3D(0, 0, 0.70)), CameraBookmarkKind.Preset),
        new("正面总览", new CameraPose(new Point3D(0, -4.2, 1.30), new Point3D(0, 0, 0.85)), CameraBookmarkKind.Preset),
        new("顶部俯视", new CameraPose(new Point3D(0.05, -0.15, 4.60), new Point3D(0, 0, 0.40)), CameraBookmarkKind.Preset),
        new("检测头特写", new CameraPose(new Point3D(0.95, -1.35, 1.45), new Point3D(0, -0.32, 1.00)), CameraBookmarkKind.Preset),
    ];

    /// <summary>书签文件完整路径。</summary>
    public string StorePath { get; }

    /// <summary>自定义书签（按保存顺序，最旧在前）。</summary>
    public IReadOnlyList<CameraBookmark> Custom => _custom;

    /// <summary>预置 + 自定义（下拉框数据源）。</summary>
    public IReadOnlyList<CameraBookmark> All => [.. Presets, .. _custom];

    public static string DefaultStorePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AoiTwin",
        DefaultFileName);

    /// <summary>从磁盘载入自定义书签；文件不存在或损坏返回 false（不抛异常）。</summary>
    public bool Load()
    {
        _custom.Clear();

        if (!File.Exists(StorePath))
        {
            return false;
        }

        StoreFile? store;
        try
        {
            store = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(StorePath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }

        if (store?.Custom is null)
        {
            return false;
        }

        foreach (var entry in store.Custom)
        {
            var pose = entry.ToPose();
            if (string.IsNullOrWhiteSpace(entry.Name) || !pose.IsValid || Find(entry.Name) is not null)
            {
                continue; // 单条损坏只丢该条
            }

            if (_custom.Count >= MaxCustomBookmarks)
            {
                break;
            }

            _custom.Add(new CameraBookmark(entry.Name!, pose));
        }

        return _custom.Count > 0;
    }

    /// <summary>
    /// 保存（或覆盖同名）自定义书签并落盘。返回保存后的书签；位姿非法或名称与预置重名时抛
    /// <see cref="ArgumentException"/>（调用方应提示用户改名，而不是静默失败）。
    /// </summary>
    public CameraBookmark Save(string name, CameraPose pose)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("书签名称不能为空", nameof(name));
        }

        if (!pose.IsValid)
        {
            throw new ArgumentException("相机位姿非法（位置与目标点重合）", nameof(pose));
        }

        var trimmed = name.Trim();
        if (Presets.Any(p => string.Equals(p.Name, trimmed, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"「{trimmed}」与预置视角重名，请换一个名称", nameof(name));
        }

        var existing = _custom.FindIndex(b => string.Equals(b.Name, trimmed, StringComparison.Ordinal));
        var bookmark = new CameraBookmark(trimmed, pose);
        if (existing >= 0)
        {
            _custom[existing] = bookmark;
        }
        else
        {
            if (_custom.Count >= MaxCustomBookmarks)
            {
                _custom.RemoveAt(0); // 淘汰最旧
            }

            _custom.Add(bookmark);
        }

        Persist();
        return bookmark;
    }

    /// <summary>删除自定义书签（预置不可删，返回 false）。</summary>
    public bool Delete(string name)
    {
        var index = _custom.FindIndex(b => string.Equals(b.Name, name, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        _custom.RemoveAt(index);
        Persist();
        return true;
    }

    public CameraBookmark? Find(string name)
        => All.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.Ordinal));

    /// <summary>原子落盘：先写 .tmp 再替换，避免断电留下半截 JSON。</summary>
    private void Persist()
    {
        var directory = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new StoreFile(
            StoreVersion,
            _custom.Select(StoreEntry.From).ToList());

        var temp = StorePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, JsonOptions));

        if (File.Exists(StorePath))
        {
            File.Replace(temp, StorePath, null);
        }
        else
        {
            File.Move(temp, StorePath);
        }
    }

    private sealed record StoreFile(int Version, IReadOnlyList<StoreEntry> Custom);

    private sealed record StoreEntry(
        string? Name,
        double Px,
        double Py,
        double Pz,
        double Tx,
        double Ty,
        double Tz,
        double Fov)
    {
        public static StoreEntry From(CameraBookmark bookmark) => new(
            bookmark.Name,
            bookmark.Pose.Position.X,
            bookmark.Pose.Position.Y,
            bookmark.Pose.Position.Z,
            bookmark.Pose.Target.X,
            bookmark.Pose.Target.Y,
            bookmark.Pose.Target.Z,
            bookmark.Pose.FieldOfView);

        public CameraPose ToPose()
        {
            var fov = double.IsFinite(Fov) && Fov is > 0 and < 180 ? Fov : CameraPose.DefaultFieldOfView;
            return new CameraPose(new Point3D(Px, Py, Pz), new Point3D(Tx, Ty, Tz), fov);
        }
    }
}
