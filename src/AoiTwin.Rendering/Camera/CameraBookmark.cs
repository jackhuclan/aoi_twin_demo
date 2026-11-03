namespace AoiTwin.Rendering.Camera;

/// <summary>书签来源：预置（代码内置，不可删）或自定义（用户保存，可持久化）。</summary>
public enum CameraBookmarkKind
{
    Preset = 0,
    Custom = 1,
}

/// <summary>相机视角书签（T1.5 / PRD FR-M1-06）。</summary>
public sealed record CameraBookmark(string Name, CameraPose Pose, CameraBookmarkKind Kind = CameraBookmarkKind.Custom)
{
    public string Name { get; init; } = string.IsNullOrWhiteSpace(Name)
        ? throw new ArgumentException("书签名称不能为空", nameof(Name))
        : Name.Trim();

    public bool IsPreset => Kind == CameraBookmarkKind.Preset;

    /// <summary>下拉框显示文本（预置不额外标注，自定义加后缀以区分可删项）。</summary>
    public string Display => IsPreset ? Name : $"{Name}（自定义）";
}
