# PRD · AOI 数字孪生一期（M1）：基础建模与数据采集

| 项 | 内容 |
|---|---|
| 文档编号 | PRD-AOI-TWIN-M1 |
| 版本 / 状态 | v1.1 / 实施完成（M1 收口，待评审签字） |
| 版本历史 | v1.0 初版评审稿；v1.1 2026-11-03 M1 实施收口：Footer 计数/时间移至文件尾（§5.5 实现口径）、占位资产包按装配态世界坐标烘焙（§5.6/asset-spec §5）、LOD2 合批代理为渲染形态（点选固定 L1 几何） |
| 上游文档 | 《AOI数字孪生_方案B_3D场景结构图》《AOI数字孪生_分期实施方案》 |
| 下游承接 | PRD-AOI-TWIN-M2、PRD-AOI-TWIN-M3 |
| 技术基线 | .NET 10 WPF · HelixToolkit（同进程）· 单台 AOI 部件级建模（30–80 节点 / 10–30 万面） |

---

## 1. 背景与目标

### 1.1 背景
现有 PL/AOI 检测上位机为纯 2D WPF 界面（配方树 / 图像映射条 / 直方图光谱区），操作与维护人员无法直观感知设备内部运动状态与部件位置。依据方案 B，采用 HelixToolkit 在上位机同进程内嵌 3D 视口，构建 AOI 整机数字孪生。一期为整个系统的地基：先完成"静态的壳 + 数据的路"，以录制回放驱动，**不依赖产线联调窗口**。

### 1.2 一期目标
1. 在 WPF 上位机内嵌 3D 渲染视口，完成 AOI 整机部件级静态建模、场景树搭建与 LOD 两档切换；
2. 打通 PLC/运动控制、相机 SDK、光源控制器、检测结果队列四路数据源的采集与统一录制入库；
3. 提供录制回放引擎，使 3D 场景可被回放数据驱动，形成"采集—录制—回放"闭环；
4. 建立贯穿三期的基础设施：PartId 台账体系、Topic 总线协议、GLB 资产管线。

### 1.3 非目标（本期不做）
- 实时轴插值动画、光源变色、Wafer Map 纹理更新（→ 二期）；
- 时序沉淀、健康度与预测分析（→ 三期）；
- 多机位接入（→ 三期后扩展，仅保证架构可扩展）。

---

## 2. 用户故事

| 编号 | 角色 | 用户故事 | 验收条件 |
|---|---|---|---|
| US-M1-01 | 产线操作员 | 作为操作员，我想在 2D 界面中一键切换到 3D 视图查看整机，以便直观了解设备布局 | 3D 视口嵌入现有 WPF 布局，切换 ≤ 1s；旋转/平移/缩放流畅 60fps |
| US-M1-02 | 产线操作员 | 作为操作员，我想通过视角书签（顶部/装载位/相机特写/光源特写）快速切换观察角度 | 四个书签一键切换，平滑过渡 ≤ 800ms |
| US-M1-03 | 设备工程师 | 作为设备工程师，我想点选 3D 上的任意部件并看到高亮与台账信息，以便快速定位部件 | 点选响应 ≤ 50ms；显示部件名/型号/位置；PartId 命中率 100% |
| US-M1-04 | 上位机开发者 | 作为开发者，我希望四路数据源由独立适配器接入统一总线，以便新增协议不动主流程 | 任一适配器可独立启停；数据源离线不影响其余通道 |
| US-M1-05 | 设备工程师 | 作为设备工程师，我想录制一个完整班次的设备数据，以便后续离线回放分析 | 录制文件完整还原班次原始数据（校验和比对通过）；丢帧率 0 |
| US-M1-06 | 工艺工程师 | 作为工艺工程师，我想以 0.5×–8× 变速回放某批次 Wafer 的加工过程 | 可按 Wafer 批次定位；时间轴拖拽正确；回放不卡 UI |
| US-M1-07 | 项目经理 | 作为项目经理，我希望建模规范（PartId、坐标原点、面数预算）在一期冻结，避免后续返工 | 规范文档 W1 评审通过并冻结；所有 Mesh 携带 PartId |
| US-M1-08 | 上位机开发者 | 作为开发者，我希望提供适配器 Mock 实现，以便无设备环境开发与测试 | 四路适配器均有 Mock；CI 可用 Mock 数据跑通全链路 |

---

## 3. 功能需求列表

优先级：P0 = 本期必须（验收门槛）；P1 = 重要（排期内完成）；P2 = 期望（可延至二期初）。

| 编号 | 功能 | 优先级 | 需求描述 |
|---|---|---|---|
| FR-M1-01 | GLB 模型资产管线 | P0 | 定义建模规范（面数预算、PartId 命名、坐标原点与单位约定）；GLB 后台线程预加载；按场景树静态挂载（Environment / AOI_Equipment 静态部分）。产出整机中模 ~15 万面 + 运动部件独立 Mesh（Robot / Stage / GateAxis_Z / PreAligner） |
| FR-M1-02 | PartId 部件台账 | P0 | 部件台账表（PartId → 名称/型号/位置/所属模块），3D 节点与台账一一对应；台账以 JSON 资产包形式随模型发布 |
| FR-M1-03 | 渲染视口与相机 | P0 | HelixToolkit 视口嵌入现有 WPF 布局；轨道相机（旋转/平移/缩放）；空场景 60fps |
| FR-M1-04 | 视角书签 | P0 | 预置书签（顶部 / 装载位 / 相机特写 / 光源特写）+ 用户自定义书签保存；切换平滑过渡 ≤ 800ms |
| FR-M1-05 | LOD 两档 | P0 | LOD1 常规 / LOD2 远景，按相机距离自动切换；远景隐藏线缆与门缝细节，运动部件保留 Transform 节点；LOD 切换无闪烁、无材质跳变 |
| FR-M1-06 | PLC/运动控制适配器 | P0 | 轴位置、IO、报警采集；协议解析与单位换算；断线重连；健康监测 |
| FR-M1-07 | 相机 SDK 适配器 | P0 | 曝光、触发、采集进度采集；同 FR-M1-06 可靠性要求 |
| FR-M1-08 | 光源控制器适配器 | P0 | 通道、亮度、频闪状态采集；同 FR-M1-06 |
| FR-M1-09 | 检测结果队列适配器 | P0 | 缺陷坐标、Wafer Map、节拍采集；同 FR-M1-06 |
| FR-M1-10 | RealtimeHub 总线雏形 | P0 | 进程内发布订阅总线；四类 Topic（Motion/Status/Optics/Result）；类型安全订阅 |
| FR-M1-11 | 环形缓冲录制 | P0 | 四类 Topic 全量落盘；按班次分文件；帧级校验和；录制启停控制与磁盘水位保护 |
| FR-M1-12 | 回放引擎 | P0 | 读录制文件重新注入 Hub（与真实数据同通道）；变速 0.5×–8×、暂停、时间轴拖拽、按 Wafer 批次定位 |
| FR-M1-13 | Raycaster 点选 + 描边 | P0 | 命中节点携带 PartId；Stencil 描边高亮；右侧信息区显示静态台账 |
| FR-M1-14 | 适配器 Mock | P1 | 四路适配器 Mock 实现 + 示例数据集（≥ 1 个完整班次的合成数据） |
| FR-M1-15 | 2D/3D 布局切换 | P1 | 与现有 WPF 主界面（配方树/图像映射条/直方图光谱区）共存的 3D 面板页，可停靠/全屏 |
| FR-M1-16 | 采集监控面板 | P1 | 各数据源连接状态、吞吐率、录制状态、磁盘剩余量的状态条 |
| FR-M1-17 | Environment 静态环境 | P1 | Cleanroom_Shell（Unlit 渐变背景、不参与拾取）、FloorGrid（地面网格/安全标线、拾取热区） |
| FR-M1-18 | 录制管理界面 | P2 | 录制文件列表、按班次/日期浏览、删除与导出 |

---

## 4. 业务流程说明

### 4.1 采集流程（主流程）
1. 上位机启动 → 配置加载（数据源地址、录制目录）→ 适配器管理器按配置实例化四路适配器；
2. 适配器依次 `StartAsync`：建立连接 → 协议握手 → 开始推送原始报文；
3. 适配器解析报文 → 换算为统一数据模型（§6.1）→ 携带 PartId 与时间戳发布至 RealtimeHub 对应 Topic；
4. RealtimeHub 将消息写入环形缓冲 → 按班次边界（换班信号或 8h 定时）滚动生成录制文件；
5. 磁盘水位 ≥ 85% 时：暂停录制并告警，**不丢弃实时消息**（内存缓冲 ≤ 60s）。

异常分支：任一数据源断线 → 适配器指数退避重连（1s/2s/4s/…上限 30s）→ 状态变更事件广播（供监控面板）；其余通道不受影响。

### 4.2 回放流程
1. 用户在录制管理界面选择录制文件（或按 Wafer 批次检索）→ 回放引擎加载（索引建立）；
2. `Play` → 回放引擎按消息原始时间戳重放，**以回放 Publisher 身份注入 RealtimeHub**（与真实采集同通道）；
3. 变速实现为时间轴缩放（0.5×–8×），不改变消息内容与顺序；
4. `Seek`：按索引二分定位 → 从目标时间点重放；时间戳统一偏移为回放时钟。

设计要点：录制—回放走同一条 Topic 通道，保证二期接入渲染层后，真实数据与回放数据无需区分处理。

### 4.3 点选流程
1. 鼠标命中 → Raycaster 求交 → 命中节点取 PartId → 查台账 → 部件 Stencil 描边；
2. 右侧信息区切换显示该部件静态台账（名称/型号/位置）；
3. 再点空白处或 Esc → 取消描边。

### 4.4 模型资产上线流程
1. W1 冻结建模规范 → 建模产出 GLB + 台账 JSON → 资产校验工具自动检查（PartId 完整性、面数预算、坐标原点）；
2. 校验通过 → 资产包发布至版本目录 → 上位机后台线程预加载 → 场景树挂载。

---

## 5. 接口与数据结构定义

> 本节为三期共用的**总线协议与数据模型基线**；二、三期仅做加法扩展，不修改既有定义。命名空间约定：`AoiTwin.Core.*`。

### 5.1 Topic 与统一消息契约

```csharp
namespace AoiTwin.Core.Bus;

public enum TwinTopic { Motion, Status, Optics, Result }   // M3 追加 Alert

public interface ITwinMessage
{
    string        PartId    { get; }   // 与部件台账一一对应；设备级消息为 "EQUIPMENT"
    DateTimeOffset Timestamp { get; }   // 设备侧时间戳；回放时由回放引擎统一偏移
}
```

### 5.2 四类消息记录

```csharp
public sealed record AxisSnapshot(
    string PartId, string AxisName,          // AxisName: R/θ/Z/X/Y/Theta/Lift…
    double Position, double Velocity,
    DateTimeOffset Timestamp) : ITwinMessage;

public sealed record StatusEvent(
    string PartId, string EventCode, Severity Severity,
    IReadOnlyDictionary<string, string> Payload,
    DateTimeOffset Timestamp) : ITwinMessage;   // 装载状态、晶圆占位、报警等

public enum Severity { Info, Warning, Alarm }

public sealed record OpticsEvent(
    string DeviceId, OpticsKind Kind,          // Kind: LightChannel/LightBrightness/CameraParam/ScanProgress
    int Channel, double Value,
    DateTimeOffset Timestamp) : ITwinMessage;

public sealed record WaferResult(
    string WaferId, WaferMapSnapshot Map,
    IReadOnlyList<DefectRecord> Defects,
    TimeSpan Tact, DateTimeOffset Timestamp) : ITwinMessage;

public sealed record WaferMapSnapshot(int SizeX, int SizeY, byte[] DieMap); // 二值/灰度 die 图
public sealed record DefectRecord(long DefectId, double X, double Y, double SizeUm, string DefectClass, int GrayLevel);
```

### 5.3 RealtimeHub 与适配器接口

```csharp
public interface IRealtimeHub
{
    void Publish<T>(TwinTopic topic, T message) where T : ITwinMessage;
    IDisposable Subscribe<T>(TwinTopic topic, Action<T> handler) where T : ITwinMessage;
    // M1 订阅方：录制器、回放引擎(反向Publish)、监控面板；M2 起增加 VisualViewModel（节流策略在 M2 扩展）
}

public enum AdapterHealth { Disconnected, Connecting, Connected, Faulted }

public interface IDeviceAdapter : IAsyncDisposable
{
    string        SourceName { get; }                 // PLC / Camera / LightSource / ResultQueue
    AdapterHealth Health      { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    event EventHandler<AdapterStateChangedEventArgs> StateChanged;
}
```

### 5.4 回放引擎接口

```csharp
public interface IReplayEngine
{
    Task LoadAsync(string recordingPath, CancellationToken ct);
    void Play(); void Pause();
    void Seek(TimeSpan replayTime);
    void SetSpeed(double factor);            // 0.5 – 8.0
    ReplayState State { get; }
    event EventHandler<ReplayProgressEventArgs> Progress;
}
public sealed record ReplayProgressEventArgs(TimeSpan ReplayTime, TimeSpan TotalTime, double Speed);
```

### 5.5 录制文件格式

- 容器：`.aotrec`（自定义分帧容器）；文件 = 文件头（版本、设备号、班次、起止时间、消息计数）+ 帧序列；
- 帧 = `[长度][CRC32][Topic][类型ID][时间戳(8B)][消息体(JSON+二进制混合：大数组如 DieMap 走裸字节)]`；
- 按班次分文件：`{设备号}_{班次开始yyyyMMdd_HHmm}.aotrec`；
- 校验：文件尾存全文件 SHA-256，回放加载时验证。

### 5.6 部件台账 JSON（随资产包发布）

```json
{
  "assetVersion": "1.0.0",
  "machine": "AOI-01",
  "parts": [
    { "partId": "ROBOT",     "name": "传输机械手", "module": "AOI_Equipment", "model": "R-XYZ-3A",
      "lod": { "l1": "robot_l1.glb", "l2": "robot_l2.glb" },
      "axes": ["R", "Theta", "Z"] },
    { "partId": "LIGHT_RING", "name": "环形光源",  "module": "Optics", "model": "LR-8CH", "channels": 8 }
  ]
}
```

### 5.7 线程模型约定（三期一致）
- **数据线程**：适配器回调 → Hub 发布（不进 UI 线程）；
- **录制线程**：Hub → 环形缓冲 → 异步落盘；
- **渲染线程**：M1 仅静态渲染；M2 起订阅节流副本做插值，绝不直连 UI 线程。

---

## 6. 非功能需求

| 类别 | 编号 | 需求 |
|---|---|---|
| 性能 | NFR-01 | 空场景（静态模型挂载完成）渲染 60fps 满帧（目标测试机： i5 + 核显） |
| 性能 | NFR-02 | GLB 后台预加载 ≤ 3s，加载期间 UI 不卡顿（无 > 100ms 阻塞） |
| 性能 | NFR-03 | Hub 发布—订阅单条消息开销 ≤ 0.1ms；录制写入不阻塞采集线程 |
| 可靠性 | NFR-04 | 采集丢帧率 0（环形缓冲 + 落盘队列确认）；断电后已落盘文件可校验修复 |
| 可靠性 | NFR-05 | 单数据源故障隔离：任一适配器崩溃/断线不影响其余通道与 UI |
| 安全 | NFR-06 | 录制文件包含产品缺陷数据：录制目录 ACL 限制为操作员组只读、工程师组读写；文件头含设备号与班次（可审计） |
| 安全 | NFR-07 | PLC/SDK 连接凭据不落明文（使用 DPAPI 加密存储于用户配置） |
| 兼容性 | NFR-08 | Windows 10 21H2+ / Windows 11；.NET 10；1920×1080 最低，支持 4K 缩放 |
| 可维护性 | NFR-09 | 适配器、Hub、回放引擎、渲染视口为独立类库（为三期"复用至 PL/EL 机台"预留） |
| 可维护性 | NFR-10 | 全链路结构化日志（采集/录制/回放/渲染分层），可按会话导出 |

---

## 7. 验收标准（DoD）

1. **功能**：FR-M1-01 ~ FR-M1-13（P0）全部通过验收测试用例；P1 功能完成或经评审批准顺延；
2. **性能**：NFR-01/02/03 达标（性能测试报告）；
3. **数据闭环**：录制 ≥ 3 个完整班次真实产线数据；任选一段回放，消息序列与录制文件逐条一致（SHA-256 + 逐帧 CRC 校验通过）；
4. **点选**：全部在册部件 PartId 命中率 100%，含 LOD2 远景档；
5. **稳定性**：连续 24h 采集+录制无崩溃、无丢帧、内存无持续增长（增长 < 50MB 视为稳定）；
6. **工程**：CI 覆盖 Mock 数据全链路冒烟测试；建模规范与台账 JSON Schema 评审冻结。

---

## 8. 里程碑计划

| 里程碑 | 周期 | 内容 | 退出条件 |
|---|---|---|---|
| MS-M1-1 资产与视口 | W1–W3 | 建模规范冻结；首版整机 GLB + 台账；HelixToolkit 视口嵌入、书签、LOD 骨架 | 空场景 60fps；点选框架可用 |
| MS-M1-2 采集与总线 | W4–W6 | 四路适配器（含 Mock）；RealtimeHub + Topic 体系；环形缓冲录制 | Mock 数据全链路打通；录制校验通过 |
| MS-M1-3 回放闭环 | W7–W8 | 回放引擎；产线首次数据采集（≥ 3 班次，夜间窗口）；录制管理界面 | 回放一致性校验通过 |
| MS-M1-4 一期验收 | W9–W10 | 集成验收、24h 稳定性测试、性能报告、缺陷修复 | §7 全部满足，评审签字 |

**风险**：① 模型还原度不足 → 规范 W1 冻结、建模与开发并行；② PLC/SDK 协议文档缺失 → Mock 优先、真实协议后补；③ 采集窗口难约 → 全程离线可开发，仅需 2–3 个夜间窗口。

---

## 9. 对下期的承诺（衔接 M2）

M1 冻结并交付给 M2 的接口契约：`ITwinMessage` 四类消息结构（§5.1–5.2）、`IRealtimeHub`（§5.3，M2 仅扩展节流订阅重载）、录制文件格式（§5.5，回放引擎继续作为回归测试手段）、PartId 台账与 GLB 资产包（运动部件 Mesh 已独立，M2 直接挂 Transform）。M2 不得修改以上契约，只能扩展。
