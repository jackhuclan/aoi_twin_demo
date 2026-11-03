# PRD · AOI 数字孪生二期（M2）：实时数据映射与状态同步

| 项 | 内容 |
|---|---|
| 文档编号 | PRD-AOI-TWIN-M2 |
| 版本 / 状态 | v1.0 / 待评审 |
| 上游文档 | PRD-AOI-TWIN-M1（接口契约承接方）、《方案B_3D场景结构图》《分期实施方案》 |
| 下游承接 | PRD-AOI-TWIN-M3 |
| 技术基线 | 同一期；新增 VisualViewModel 层、GPU 上传队列、FX 按需渲染框架 |

---

## 1. 背景与目标

### 1.1 背景
一期已完成"静态的壳 + 数据的路"：3D 静态场景、四路适配器、RealtimeHub 录制回放闭环。本二期把方案 B 的四层数据链路（设备实时层 → RealtimeHub → VisualViewModel → 3D 渲染层）完整跑通，使 3D 场景从"静态模型"升级为物理设备的**实时镜像**。

### 1.2 二期目标
1. 启用节流分发与 VisualViewModel 层：轴数据 200ms → 60fps 插值，事件直推，渲染与数据线程彻底分离；
2. 实现运动同步（Robot / Stage / GateAxis_Z / PreAligner）、光学状态映射（8 通道光源变色、视野锥）、Wafer Map 纹理与缺陷点云增量更新；
3. 打通点选钻取与 2D/3D 双向联动四条链路，跟随模式上线；
4. FX 按需渲染框架落地（描边/报警脉冲/传输粒子），通过 72h 真机长跑验收。

### 1.3 非目标（本期不做）
- 时序持久化、健康度、预测模型（→ 三期）；
- LOD 第三档（特写 PBR）与静态合批终调（→ 三期，本期维持两档）。

---

## 2. 用户故事

| 编号 | 角色 | 用户故事 | 验收条件 |
|---|---|---|---|
| US-M2-01 | 产线操作员 | 作为操作员，我想看到机械手/载台随实际轴数据同步运动，以便确认设备动作与程序一致 | 插值运动无抖动；虚拟位置与实际偏差 ≤ 1 个轴数据周期 |
| US-M2-02 | 产线操作员 | 作为操作员，我想看到光源通道切换时 3D 上即时变色，以便确认当前成像通道 | 切换到变色延迟 ≤ 100ms；8 通道颜色映射与配置一致 |
| US-M2-03 | 产线操作员 | 作为操作员，我想看到相机视野锥和扫描进度线，以便了解当前正在扫描的晶圆区域 | 视野锥准确覆盖扫描区域；进度线随采集推进（500ms 刷新） |
| US-M2-04 | 工艺工程师 | 作为工艺工程师，我想在 3D 晶圆上看到 Wafer Map 与缺陷点实时累积，以便监控检测进展 | 整片 Map 刷新掉帧 < 5fps；缺陷点追加为 O(增量) |
| US-M2-05 | 设备工程师 | 作为设备工程师，我想点选 3D 部件后在右侧直方图/光谱区看到该部件实时详情（IO 状态、寿命计数、最近报警） | 点选后面板切换 ≤ 200ms；数据为实时订阅 |
| US-M2-06 | 设备工程师 | 作为设备工程师，我想右键部件直接跳转对应 2D 诊断页（相机参数/光源控制/轴调试） | 按部件类型跳转正确页面 |
| US-M2-07 | 工艺工程师 | 作为工艺工程师，我想在 2D 缺陷列表选中一条后，3D 晶圆对应坐标点高亮，点击高亮点调出缺陷原图 | 2D→3D 高亮 ≤ 100ms；原图放大窗口正确关联 DefectId |
| US-M2-08 | 产线操作员 | 作为操作员，我想开启跟随模式锁定视角追踪载台/机械手运动 | 追踪平滑无抖动；可随时解除 |
| US-M2-09 | 产线操作员 | 作为操作员，我想在部件报警时看到 3D 上的脉冲提示，以便第一时间注意到异常 | 脉冲与 PLC 报警同步触发；解除后停止 |
| US-M2-10 | QA 工程师 | 作为 QA，我要求系统 72h 真机连续运行不崩溃、帧率不衰减 | 72h 长跑：无崩溃、无内存泄漏（增长 < 100MB）、帧率衰减 < 5% |

---

## 3. 功能需求列表

| 编号 | 功能 | 优先级 | 需求描述 |
|---|---|---|---|
| FR-M2-01 | 节流分发正式版 | P0 | Hub 订阅支持 ThrottlePolicy：轴数据 200ms 合并（保留最新）、状态/参数事件直推不过滤、WaferResult 按片整片推送；策略可配置化（配置文件热加载） |
| FR-M2-02 | VisualViewModel 层 | P0 | 3D 专属 VM：Transform 副本（仅位姿）、状态色值/材质参数、Wafer Map 纹理与缺陷点缓冲的 GPU 上传队列；与数据线程零共享可变状态；绝不直连 UI 线程 |
| FR-M2-03 | 运动同步 | P0 | Robot(R/θ/Z)、Stage(X/Y/Z/θ+升降)、GateAxis_Z、PreAligner 挂 Transform 驱动；示教坐标 → 模型坐标换算；2–5Hz 轴数据 → 60fps 渲染线程插值 |
| FR-M2-04 | 光源通道变色 | P0 | Light_Ring 8 通道事件变色（通道→颜色映射表可配置）；Light_Coax 亮度变更反映到自发光强度 |
| FR-M2-05 | 视野锥与扫描进度 | P0 | Camera_LineScan Frustum 视野锥可视化当前扫描区域；扫描进度线随采集推进（500ms） |
| FR-M2-06 | Wafer Map 纹理更新 | P0 | Wafer_Current 整片事件走 GPU Texture 替换（不重建几何体）；支持二值/灰度 DieMap |
| FR-M2-07 | 缺陷点云增量 | P0 | 缺陷散点 InstancedMesh + BufferAttribute 局部追加（O(增量)）；支持按缺陷等级着色 |
| FR-M2-08 | 部件钻取面板联动 | P0 | 点选部件 → 右侧直方图/光谱区切换为部件详情（IO 状态、寿命计数、最近报警），实时订阅该部件 Topic |
| FR-M2-09 | 右键跳转诊断页 | P1 | 右键部件 → 按部件类型跳转 2D 诊断页（相机参数/光源控制/轴调试） |
| FR-M2-10 | 缺陷 2D/3D 联动 | P0 | 2D 缺陷列表选中 → 3D 坐标点高亮 → 点击高亮点 → 缺陷原图放大窗口 |
| FR-M2-11 | 跟随模式 | P1 | 视角锁定载台/机械手实时追踪；解除恢复自由视角 |
| FR-M2-12 | FX 按需渲染框架 | P0 | FX_Overlay：有则渲染、无则不进 Draw Call；首批特效：选中描边（复用 M1 Stencil）、报警脉冲、传输粒子流 |
| FR-M2-13 | 晶圆占位与装载状态 | P1 | Track 晶圆占位信号、LoadPort 装载状态（Cassette 25 片）事件驱动 3D 状态变化 |
| FR-M2-14 | 回放回归测试模式 | P1 | 回放引擎驱动全部 FR-M2-03~07 的自动化回归（一期录制数据为测试资产） |

---

## 4. 业务流程说明

### 4.1 实时数据主链路（核心流程）
1. 一期适配器采集（不变）→ Hub `Publish` 至四类 Topic；
2. Hub 节流层按订阅方策略分发：轴订阅者收到 200ms 合并后的最新快照；事件订阅者即时收到；结果订阅者按片收到完整 `WaferResult`；
3. VisualViewModel 收到副本后：位姿写入 Transform 副本缓冲；状态色写入材质参数缓冲；Wafer Map/缺陷点进入 **GPU 上传队列**；
4. 渲染线程 60fps 循环：场景树遍历 + LOD 选择 → 对运动部件执行 `Sample(now)` 插值取位姿 → 消费 GPU 上传队列 → FX 层按需渲染；
5. UI3D 标注层 1s 节流投影（本期仅部件名，KPI 标注 → 三期）。

延迟预算：设备 → Hub ≤ 50ms（事件）/ 200ms（轴）；Hub → 渲染呈现 ≤ 100ms。

### 4.2 部件钻取流程
1. Raycaster 命中 PartId（一期框架）→ 描边 + 台账（一期）；
2. **新增**：Hub 建立该 PartId 的临时订阅（IO 状态/寿命计数/报警）→ 右侧面板切换为部件详情视图；
3. 取消选中 → 临时订阅释放、面板恢复默认。

### 4.3 缺陷联动流程（双向）
- 2D→3D：缺陷列表选中 DefectId → VM 在点云缓冲中定位实例索引 → 高亮（改实例色/放大）→ 用户点击高亮点 → 调出缺陷原图窗口（关联灰度图存储路径）；
- 3D→2D：点选晶圆上缺陷点 → 2D 列表滚动定位到对应行。

### 4.4 报警脉冲流程
PLC 报警 StatusEvent(Severity=Alarm) → Hub 直推 → FX 框架在该 PartId 部件挂载脉冲材质（世界空间外发光闪烁）→ 报警解除事件 → 摘除特效。

### 4.5 真机联调流程（2 个窗口）
窗口 A（W4–W6）：验证运动同步/光学映射/晶圆数据三链路，现场标定示教坐标换算系数；
窗口 B（W10–W12）：72h 长跑 + 验收测试。窗口间开发以一期录制数据回放为主。

---

## 5. 接口与数据结构定义

> 承接 PRD-M1 §5 全部契约（`ITwinMessage`、四类消息、`IRealtimeHub`、`IDeviceAdapter`、`IReplayEngine`），本节仅定义**新增与扩展**部分。

### 5.1 RealtimeHub 扩展：节流订阅

```csharp
namespace AoiTwin.Core.Bus;

public sealed record ThrottlePolicy(
    TimeSpan MinInterval,     // 合并窗口：轴 200ms；TimeSpan.Zero = 事件直推
    bool    KeepLatest = true // 窗口内保留最新值（轴场景）；false = 逐条排队
);

public interface IRealtimeHub   // 在 M1 接口上扩展（Publish/Subscribe 原签名不变）
{
    IDisposable Subscribe<T>(TwinTopic topic, Action<T> handler, ThrottlePolicy? policy) where T : ITwinMessage;
}
```

### 5.2 VisualViewModel 契约

```csharp
namespace AoiTwin.ViewModel;

/// 运动部件 VM：数据线程写副本，渲染线程采样插值（双缓冲，无锁交换）
public interface IMotionPartViewModel
{
    string PartId { get; }
    void   Push(AxisSnapshot[] axes);                       // 数据线程调用
    bool   Sample(DateTimeOffset renderTime, out Matrix4x4 pose); // 渲染线程调用
}

/// 光学部件 VM：状态 → 材质参数
public interface IOpticsPartViewModel
{
    string PartId { get; }
    void   Apply(OpticsEvent evt);          // 通道→颜色映射由配置表驱动
}

/// 晶圆数据 VM：GPU 上传队列
public interface IWaferDataViewModel
{
    void SetWaferMap(WaferMapSnapshot map);           // 入队整片纹理替换
    void AppendDefects(IReadOnlyList<DefectRecord>);  // 入队实例增量
    void HighlightDefect(long defectId);              // 联动高亮
    event EventHandler<DefectClickedEventArgs> DefectClicked; // 3D→2D 联动
}
```

### 5.3 坐标换算与插值

```csharp
namespace AoiTwin.Core.Kinematics;

/// 示教坐标 → 模型坐标换算（系数标定文件随资产包发布，M2-W1 用录制数据标定）
public interface ITeachCoordinateMapper
{
    Matrix4x4 ToModelPose(string partId, IReadOnlyDictionary<string, double> axisPositions);
}

public interface ITransformInterpolator   // Catmull-Rom / 线性可配；单调时间戳，防回跳
{
    void Push(string axisName, double value, DateTimeOffset t);
    double Sample(string axisName, DateTimeOffset renderTime);
}
```

### 5.4 FX 框架与钻取

```csharp
namespace AoiTwin.Rendering.Fx;

public interface IFxOverlay
{
    IDisposable Attach(PartFxKind kind, string partId);   // 返回句柄，释放即摘除
}
public enum PartFxKind { SelectionOutline, AlarmPulse, TransferParticles }

namespace AoiTwin.Panel;

public interface IPartDetailDrillDown
{
    void Open(string partId);      // 建立临时订阅并切换右侧面板
    void Close();
}
```

### 5.5 颜色映射配置（随配方发布）

```json
{
  "lightChannelColors": { "1": "#FFE9C4", "2": "#C4E9FF", "3": "#E9C4FF", "4": "#C4FFE1",
                          "5": "#FFC4C4", "6": "#F5F5C4", "7": "#C4D6FF", "8": "#D6FFC4" },
  "defectClassColors":  { "Scratch": "#FF6B81", "Particle": "#FF9F43", "Residue": "#C56CF0" },
  "stateColors":        { "Run": "#2ED573", "Idle": "#8395A7", "Alarm": "#FF4757" }
}
```

---

## 6. 非功能需求

| 类别 | 编号 | 需求 |
|---|---|---|
| 性能 | NFR-11 | 真机实时工况下整机渲染 ≥ 60fps（LOD1 常规档）；Wafer Map 整片刷新掉帧 < 5fps |
| 性能 | NFR-12 | 端到端延迟：事件 ≤ 150ms、轴（插值后视觉跟随）≤ 300ms、2D→3D 缺陷高亮 ≤ 100ms |
| 性能 | NFR-13 | Hub→VM 全链路零 GC 抖动：渲染帧内禁止堆分配（热路径使用缓冲池/struct） |
| 可靠性 | NFR-14 | 72h 真机长跑：无崩溃、无未处理异常、内存增长 < 100MB、帧率衰减 < 5% |
| 可靠性 | NFR-15 | 渲染线程与数据线程隔离：数据线程阻塞不得导致渲染冻结（反之亦然）；异常被 VM 边界捕获并降级（显示最近有效状态 + 告警角标） |
| 安全 | NFR-16 | 缺陷原图访问沿用一期 ACL；3D 联动调图走同一鉴权通道 |
| 兼容性 | NFR-17 | 低端核显机型（一期 NFR-08 基准机）实时工况 ≥ 30fps，自动降级路径（粒子/描边可关闭） |
| 可维护性 | NFR-18 | 节流策略、颜色映射、坐标标定系数均为配置文件，修改不需重编译 |

---

## 7. 验收标准（DoD）

1. **功能**：FR-M2-01 ~ 08、10、12（P0）通过验收测试；P1 完成或经批准顺延至三期初；
2. **同步精度**：四轴运动部件插值无可见抖动；虚拟/实际偏差 ≤ 1 个轴数据周期（现场标定报告）；
3. **联动**：四条联动链路（部件钻取/右键诊断/缺陷双向/跟随）演示通过；
4. **性能**：NFR-11 ~ 13、17 达标（性能测试报告，含真机与核显机两档）；
5. **长跑**：72h 真机连续运行报告（NFR-14）；
6. **回归**：回放模式自动回归套件覆盖 FR-M2-03~07，接入 CI；
7. 两个真机联调窗口完成，示教坐标标定文件入库。

---

## 8. 里程碑计划

| 里程碑 | 周期 | 内容 | 退出条件 |
|---|---|---|---|
| MS-M2-1 数据管线升级 | W1–W3 | 节流分发 + VisualViewModel 骨架 + 运动同步（回放数据调通插值）；坐标换算标定 | 回放数据驱动四部件运动无抖动 |
| MS-M2-2 状态与晶圆 | W4–W6 | 光学变色 + 视野锥 + Wafer Map 纹理 + 缺陷点云；**真机联调窗口 A** | 真机三链路演示通过；标定系数入库 |
| MS-M2-3 联动与特效 | W7–W9 | 点选钻取四链路 + 跟随模式 + FX 框架与首批特效 | 联动演示通过；特效开关不影响基线帧率 |
| MS-M2-4 验收 | W10–W12 | **真机联调窗口 B：72h 长跑**、性能调优、验收评审 | §7 全部满足，评审签字 |

**风险**：① 轴坐标与模型坐标不一致 → 换算模块独立成库，W1 用录制数据标定；② 长跑内存泄漏/帧率衰减 → W4 起每次联调附带 24h 压测，问题当周闭环；③ 联调窗口受限 → 开发主体在回放数据上完成，真机压缩至 2 个窗口。

---

## 9. 对下期的承诺（衔接 M3）

M2 冻结并交付给 M3：节流分发与 VisualViewModel 契约（§5.1–5.2，M3 只读复用）、GPU 上传队列与 FX 框架（M3 的预警色/热力晕染/KPI 标注作为新 FX Kind 挂载）、钻取面板框架（M3 升级为健康度视图）。M3 允许的改动仅为：Hub 新增订阅方（持久化 Sink）、新增 `Alert` Topic 与对应 FX Kind、钻取面板内容扩展——不修改 M2 既有契约。
