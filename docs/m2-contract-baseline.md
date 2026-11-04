# M2 契约基线（TA.0）

> 目的：在动 M2 代码前，把「哪些契约已冻结、哪些本期新增、哪些要改」写成一份可核对的清单，
> 避免边写边改导致 M1 回放/录制链路回归。
> 核对方式：逐条对照源码，不引用 PRD 文本作为事实来源。

## 1. M1 已冻结契约（**只增不改**）

| 契约 | 位置 | M2 是否改动 |
|---|---|---|
| `ITwinMessage { PartId, Timestamp }` | `Core/Bus/ITwinMessage.cs` | 不改（`WaferResult.PartId` 仍固定 `WAFER_CURRENT`） |
| `AxisSnapshot(PartId, AxisName, Position, Velocity, Timestamp)` | `Core/Bus/Messages.cs` | 不改 |
| `StatusEvent(PartId, EventCode, Severity, Payload, Timestamp)` | 同上 | 不改 |
| `OpticsEvent(PartId, Kind, Channel, Value, Timestamp)` | 同上（`OpticsKind` 四值） | 不改 |
| `WaferResult(WaferId, Map, Defects, Tact, Timestamp)` + `WaferMapSnapshot` + `DefectRecord` | 同上 | 不改 |
| `TwinTopic { Motion, Status, Optics, Result }` | `Core/Bus/TwinTopic.cs` | 不改（M3 才追加 `Alert`） |
| `IRealtimeHub.Subscribe<T>(topic, handler)` | `Core/Bus/IRealtimeHub.cs` | **保留原签名**，新增重载 |
| `.aotrec` 分帧容器（CRC32 + SHA-256 + 断电修复） | `Core/Recording/*` | 不改 |
| `ReplayEngine` / `ReplaySession` | `Core/Replay/*` | 不改（M2 只作为数据源消费） |
| `PartCatalogEntry(PartId, Name, Module, Model, Lod, Axes)` | `Core/Catalog/PartCatalog.cs` | 结构不改（`Axes` 字段 M1 已预留，本期真正启用） |
| 装配树 `SceneRoot → 部件节点(Transform) → Model3DGroup → GeometryModel3D(PartId)` | `Rendering/Scene/SceneTreeBuilder.cs` | 结构不改，**追加**旋转通道（见 §3） |
| 点选固定 L1 几何、反向壳描边 | `Rendering/Interaction/*` | 不改（M2 的 FX 复用） |

## 2. M2 新增契约

| 契约 | 位置 | 说明 |
|---|---|---|
| `ThrottlePolicy(MinInterval, KeepLatest)` | `Core/Bus/ThrottlePolicy.cs` | 合并窗口；`TimeSpan.Zero` = 直推 |
| `IRealtimeHub.Subscribe<T>(topic, handler, policy)` | 同上工程 | 原签名改为委托到 `policy: null` |
| `IMotionPartViewModel` / `IOpticsPartViewModel` / `IWaferDataViewModel` | `AoiTwin.ViewModel`（新工程） | 数据线程写副本、渲染线程采样 |
| `ITeachCoordinateMapper` / `ITransformInterpolator` | `Core/Kinematics/*` | 示教坐标 → 模型位姿；线性插值 |
| `IFxOverlay` / `PartFxKind` | `Rendering/Fx/*` | 枚举只追加不改名（M3 预留） |
| `IPartDetailDrillDown` | `AoiTwin.ViewModel`（`Panel/`） | 临时订阅 + 面板切换 |
| `LightChannelPalette` / `RgbColor` / `LightChannelPaletteFileSource` | `AoiTwin.ViewModel` | PRD §5.5 八通道色；文件热加载坏配置保旧值 |
| `DefectIndexEntry` / `IDefectPickSource` | `Rendering/Dynamics/*`（TA.14） | **DefectId 是 2D 列表与 3D 点云的唯一主键**：索引项含池序号/块号/块内偏移/世界坐标；上层只经 `IDefectPickSource` 访问 3D 端（风险 R-4「禁止各链路私连」） |
| `DefectListViewModel` / `DefectRow`（TA.14） | `AoiTwin.ViewModel` | 2D 缺陷列表：整片替换 + 滑窗（`MaxRows=1000`），窗外 DefectId 定位时自动滚窗，容量恒定 |
| `DefectLinkService` / `DefectImageViewModel`（TA.14） | `AoiTwin.App` | 2D↔3D 联动编排（高亮 ≤100ms、射线反查容差 6mm = `DefectPointDriver.DefaultPickTolerance`）与原图窗口数据 |
| `HandlingStateViewModel`（TA.16） | `AoiTwin.ViewModel` | **Status Topic 负载契约**：`TRACK` 用 `WaferPresent`（"1"/"true"/"on"，大小写不敏感）；`LOAD_PORT` 用 `CassetteCount` + `CassetteCapacity`（缺省 25）。缺键/非法值保持旧值（NFR-15 降级） |
| `HandlingSyncDriver`（TA.16） | `Rendering/Dynamics` | Track 晶圆占位壳按需 attach/detach（无片退出 Draw Call）；Cassette 槽位共享一份冻结几何、只换材质引用；**状态应用 1s 节流**（`ApplyInterval`），首帧无条件应用 |

## 3. 对既有实现的追加（不改契约，只加成员）

| 追加 | 原因 |
|---|---|
| `PartVisual.Rotation`（`RotateTransform3D?`）与 `PartVisual.Transform`（`Transform3DGroup`） | 几何按世界坐标烘焙，绕自身中心旋转必须 `T(c)·Rz·T(-c)`；M1 只有 `TranslateTransform3D` 无法表达 θ 轴 |
| `MotionSyncDriver` 消费 `Matrix3D` 而非仅位移 | 同上 |

**兼容性**：`PartVisual.Motion` 保留（M1 的 `BuildPicker` 与测试继续可用），新成员为 `null` 时行为与 M1 完全一致。

## 4. 决策 D-1：部件台账对齐数据侧命名

**问题**：M1 台账用 `GATE_AXIS_X`/`WAFER_STAGE`/`CAMERA_UNIT`/`HEAD_AXIS_Z`；
已下发的 `assets/mock-dataset/mock-scenario.json` 与四路适配器用 `ROBOT`/`STAGE`/`CAMERA`。

**决策**：台账扩到 M2 部件集（`assetVersion 0.2.0`），**数据侧命名优先**，M1 三个部件改名：

| M1 | M2 | 理由 |
|---|---|---|
| `WAFER_STAGE` | `STAGE` | mock 场景已按 `STAGE` 发轴 |
| `CAMERA_UNIT` | `CAMERA` | `CameraSdkAdapter.Parse` 固定发 `"CAMERA"` |
| `HEAD_AXIS_Z` | `GATE_AXIS_Z` | 对齐 PRD §5.3 的 `GateAxis_Z` |

**M2 部件集（16 件 / 11 轴）**：

| PartId | 名称 | module | axes |
|---|---|---|---|
| `BASE_FRAME` | 整机机架 | FRAME | — |
| `GATE_AXIS_X` | 龙门 X 轴 | MOTION | X |
| `GATE_AXIS_Y` | 横梁 Y 轴 | MOTION | Y |
| `GATE_AXIS_Z` | Z 轴升降头 | MOTION | Z |
| `ROBOT` | 搬运机械手 | MOTION | R, THETA, Z |
| `STAGE` | 晶圆载台 | STAGE | X, Y, Z, THETA |
| `PRE_ALIGNER` | 预对准台 | STAGE | THETA |
| `CAMERA` | 检测相机 | OPTICS | — |
| `CAMERA_LINE_SCAN` | 线扫相机 | OPTICS | — |
| `LIGHT_RING` | 环形光源（8 通道） | OPTICS | — |
| `LIGHT_COAX` | 同轴光源 | OPTICS | — |
| `WAFER_CURRENT` | 当前晶圆 | WAFER | — |
| `TRACK` | 晶圆传输轨道 | HANDLING | — |
| `LOAD_PORT` | 上下料口 | HANDLING | — |
| `SAFETY_DOOR` | 前安全门 | FRAME | — |
| `SIGNAL_TOWER` | 三色警示灯 | STATUS | — |

**运动部件 6 个**（PRD §5.3 要求 4 个，`GATE_AXIS_X/Y` 为额外覆盖）：`ROBOT`、`STAGE`、`GATE_AXIS_Z`、`PRE_ALIGNER`、`GATE_AXIS_X`、`GATE_AXIS_Y`。

**代价与处置**：`PlaceholderAssets.Specs()` 重写为 16 件并整体重生成（占位包可幂等重建），
4 个测试文件中的部件清单断言同步更新（`SceneLoadTests`/`PartPickTests`/`LodTests`/`UiSmokeTests`）。
真实资产到位后只换 GLB，不改代码。

## 5. 验收判据（供 CI）

1. `dotnet test` 全绿（含更新后的部件清单断言）；
2. `AssetTools verify` 对新占位包通过；
3. `AssetTools` 报告的部件数 = 16、轴数 = 11；
4. M1 的录制/回放往返用例不回归。
