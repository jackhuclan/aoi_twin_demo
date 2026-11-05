# AOI 数字孪生架构白皮书 v0.1（M1 收口 + M2 / M3 增量）

> 目标读者：参与 M2/M3 开发与三期「复用至 PL/EL 机台」的工程师。
> 本文说明冻结的类库边界、数据链路与扩展点。§1–§7 为 M1 收口版原文（仍有效），
> §8 为 M2 交付的架构增量，§9 为 M3 交付的架构增量（预测分析与工艺辅助）。
> 阅读顺序建议：M1 收口 → §8（若要理解 3D 实时链路）→ §9（若要接分析/存储/插件）。

## 1. 总体结构

```
┌────────────────────────────────────────────────────────────┐
│ AoiTwin.App（WPF 宿主：DI 组装、页签、主题、视图模型）        │
│   ├─ TwinSceneViewModel / CollectionMonitor / Replay / …   │
└──────┬──────────────────────┬───────────────────────────────┘
       │                      │
┌──────▼──────────┐   ┌───────▼───────────────────────────────┐
│ AoiTwin.Rendering│  │ AoiTwin.Adapters (+ .Mock)             │
│ 视口/相机/LOD/    │  │ 适配器基类（退避重连/健康状态机）         │
│ 点选/帧探针       │  │ PLC / Camera / Light / ResultQueue     │
└──────┬──────────┘   └───────┬───────────────────────────────┘
       │ 场景数据               │ ITwinMessage
┌──────▼──────────────────────▼───────────────────────────────┐
│ AoiTwin.Core                                                │
│  Bus(TwinTopic/四类消息/RealtimeHub) · Recording(.aotrec)    │
│  Catalog(PartCatalog) · Replay(ReplayEngine/批次索引)        │
│  Serialization(TwinJson 契约冻结)                            │
└─────────────────────────────────────────────────────────────┘
```

**依赖单向**：App → Rendering/Adapters → Core。Core 不引用任何 WPF/Helix 类型，
是三期跨机台复用的最小集（见 §5）。

## 2. 数据链路（采集方向）

```
设备 → 适配器(AdapterBase) ──MessageEmitted──▶ RealtimeHub ──Subscribe──▶ ① 录制器(.aotrec)
                                                │                          ② 采集监控(统计快照)
                                                └──────── M2: VisualViewModel(运动映射) ◀──
```

- **适配器**：`AdapterBase` 提供 1s→30s 指数退避重连、健康状态机（Connected/Reconnecting/…）、
  订阅方异常隔离（单源故障不影响其余通道，NFR-05）。协议实现只写传输层（`ITransport`），
  解析与映射（如 PLC i32 µm→mm）在适配器内完成。
- **RealtimeHub**：类型 + Topic 过滤的进程内总线，同步直调、快照查询、零丢包（NFR-03：0.49µs/条）。
- **消息契约**：`AxisSnapshot/StatusEvent/OpticsEvent/WaferResult` 四类 record，
  JSON 行为属 T1.0 冻结范围——**变更即变更 .aotrec 文件格式，必须走契约评审**（`TwinJson`）。

## 3. 数据链路（回放方向）

```
.aotrec ─▶ RecordingReader(逐帧 CRC + Footer SHA 校验) ─▶ ReplayEngine(变速/Seek/时钟重映射)
                │                                             │
                └─ WaferBatchIndex(批次区间)              ReplaySession ─▶ 逐条 Publish 回 Hub
```

- **录制**：分帧 20B 头（len/CRC/topic/typeId/ts）+ payload；Footer = magic + endTs + count +
  全文件 SHA-256。写入端加锁串行化（四路并发安全）；水位 85% 暂停；2h/班次滚动。
- **断电修复**：`Inspect`（扫描有效前缀）/`Repair`（重建 Footer）。
- **回放**：消息以 `with { Timestamp }` 重映射到回放时钟后回灌 Hub——**回放与实时共用同一套
  下游消费者**，M2 的运动映射无需区分数据来源。

## 4. 3D 渲染线（Rendering）

- **TwinViewport3D**：Z 向上米制、等轴测默认相机、地面网格 + 占地轮廓、每帧回调
  （M2 运动学插值直接取渲染节拍，不另起计时器）。
- **资产链路**：`GlbAssetLoader`（并行后台解析、部件级进度、异常带 PartId）→
  `SceneTreeBuilder`（部件节点 + `TranslateTransform3D` 运动挂点 + Mesh 挂 PartId 附加属性）。
- **LOD**：`LodPackage` 同持 L1 全保真与 L2 合批代理；**点选始终用 L1 几何**
  （`PartPicker` + 纯数学 Möller–Trumbore），LOD2 只是渲染形态——命中率与档位解耦。
  远档 Draw Call 10→5（−50%）。
- **点选**：视口点击 → `RayFrom` 射线 → `PartPicker` 最近命中 → 选中描边（反向壳，WPF 无
  stencil 的等效实现）→ 台账详情。
- **帧探针**：`FrameTimeSampler`（纯计算，120 帧滚动 FPS/P95/Max）+ `FrameTimeProbe`
  （CompositionTarget.Rendering 挂点、250ms 节流）——NFR-01/02 判据常驻 UI。

## 5. 三期复用边界（为 PL/EL 机台预留）

| 复用层 | 内容 | 复用方式 |
|--------|------|----------|
| Core | 消息契约、Hub、.aotrec、回放引擎 | 原样引用（零修改） |
| Adapters | 基类 + 协议骨架 | 新机台只新增传输实现 |
| Rendering | 视口/LOD/点选/探针 | 更换资产包（台账 + GLB）即可 |

**新增机台的接入工作量 = 一份台账 JSON + 一组 GLB + 若干 ITransport 实现**；
M2 的运动映射、M3 的分析都建立在 PartId 主键上，机台切换不影响上层。

## 6. M1 冻结的关键决策（M2/M3 不得隐式破坏）

1. 四类消息 + JSON 序列化行为（.aotrec 格式）——契约变更须评审；
2. 台账 schema（`part-catalog.schema.json`）——新增字段向后兼容；机械零位点在 M2 补 `pivot` 或由包围盒推算（asset-spec §5）；
3. 装配树约定：部件节点携带运动平移变换、Mesh 带 PartId——M2 只写 Offset，不重建树；
4. 点选集合固定 LOD1 几何；
5. 录制目录：现场班次 `assets/recordings/`（清单登记、不入 Git），Mock 演示写 `%LOCALAPPDATA%\AoiTwin\mock-recordings`；
6. 版本单一来源 `Directory.Build.props`；CPM 集中包管理（`Directory.Packages.props`）。

## 7. 已知边界与 M2 展望

- 适配器均为 Mock 传输；真实 PLC/SDK 协议在 T3.4 前补齐（风险项见实施计划 §8）；
- 稳定性：`tools/AoiTwin.Soak soak` 已实测 15min / 20min 两轮（丢帧 0、内存第 6 分钟走平、
  峰值增长 13MB，见 `docs/m1-stability-report.md`）；24h 全量夜间执行，判据由工具自动输出；
- 性能：NFR-02/03 均已实测达标，NFR-01 60fps 待基准机 60Hz 本地会话复测
  （本机远程会话裸渲染循环仅 ~32fps，无法作有效判据，见 `docs/m1-perf-report.md`）；
- M2 主题：Hub → VisualViewModel → 部件节点 Offset 的实时运动映射、状态徽标、缺陷热力叠加；
- M3 主题：批次时序档案、光衰预测、缺陷趋势（`MockScenario.decayChannel` 已埋素材）。

---

## 8. M2 架构增量（实时数据映射与状态同步）

### 8.1 新增一层：AoiTwin.ViewModel

```
AoiTwin.App ──▶ AoiTwin.Rendering ──┐
    │                                ├──▶ AoiTwin.ViewModel ──▶ AoiTwin.Core
    └────────────────────────────────┘
```

`AoiTwin.ViewModel` 是 **net10.0 纯数据工程，不引用 WindowsBase**：位姿引擎、双缓冲快照、
上传/缺陷队列、配色表、钻取与缺陷列表都在这里，可被无头测试直接驱动。
它同时是「渲染线程 ↔ 数据线程」的唯一汇合点——数据线程只写不可变快照，渲染线程只读引用。

| 关注点 | 落点 | 关键约束 |
|---|---|---|
| 位姿 | `MotionPoseEngine` + `PoseSnapshot` | 每部件双缓冲（两块 16 元素缓冲轮换写、写完换引用），帧内零分配 |
| 运动学 | `Core/Kinematics`（标定换算 + 环形 32 样本插值） | 缺标定键恒等降级，不阻塞渲染 |
| 队列 | `WaferUploadQueue`（有界 8 片）、`DefectBatches` | 同源双发：纹理与点云各自消费，互不抢占 |
| 配色 | `LightChannelPalette` + 文件源 | 三表（通道/缺陷等级/状态），热加载，坏配置保旧值 |

### 8.2 节流链路

```
适配器 ──Publish──▶ RealtimeHub ──ThrottledSink(策略) ──▶ TwinVisualViewModel
                          ▲                    │
                ThrottlePolicyFileSource       └ 轴：200ms 窗口 **按键合并**（PartId|AxisName）
                  （热加载，坏配置保旧值）           状态/光学/结果：直推不过滤
```

**坑（已用测试锁住）**：策略文件只能表达「窗口 + KeepLatest」，表达不了 `MergeKey`（委托）。
不补默认合并键，同一窗口内 X/Y/Z 会互相顶掉——逐轴消息只剩最后一条，等于丢轴。
`TwinVisualViewModel.NormalizeMotionPolicy` 统一补键。

### 8.3 渲染层：驱动器 + 叠加视觉

| 驱动器 | 职责 | 关键设计 |
|---|---|---|
| `MotionSyncDriver` | 每帧把位姿写进 `TranslateTransform3D.Offset` | 只改 Offset，不重建树；LOD2 按运动组找驱动件随动 |
| `OpticsSyncDriver` | 8 通道变色 + 同轴亮度 | M1 材质已 `Freeze()` → 走叠加视觉（8 段程序化环 + 发光壳），基线材质零侵入 |
| `ScanFrustumDriver` | 视野锥 + 扫描进度线 | 3 个 `LinesVisual3D`；`LinesVisual3D` 是 `Visual3D` 不是 `Model3D` |
| `WaferMapDriver` | 整片纹理替换 | `WriteableBitmap.WritePixels`，几何与材质引用零重建 |
| `DefectPointDriver` | 点云增量 | 按等级分池、512 点/块、批内 `Buffer/Flush`（逐点 `Add` 是 O(N²)，1 万点 1070ms） |
| `HandlingSyncDriver` | 晶圆占位 + Cassette 槽位 | 占位壳按需 attach/detach；25 槽共享一份冻结几何只换材质；状态 1s 节流 |

**FX 按需渲染**：`FxOverlay` 独立子树，空 FX 时整体退出 `OverlayRoot`（不进 Draw Call）；
句柄持有内容引用，被同键替换后变 no-op（否则旧句柄会误删新内容）。

### 8.4 DI 纪律（一次真实事故的教训）

M2 曾出现**假接线**：`App` 注册了引擎与配色，但 `TwinSceneViewModel` 有无参构造，
容器选了它 → 自建 hub + 恒等标定引擎。界面照常打开、单测全绿，真机数据一条都到不了 3D，
且 µm 被当米（位移放大 1e6 倍）。

现在的约束：

1. 注册表只有一份（`AppServices.AddAoiTwinApp`），`App` 与 `DiCompositionTests` 共用；
2. ViewModel 的 DI 构造**只接收服务参数**，不自建基础设施；
3. 接口型依赖必须显式注册（只注册具体类型时，可选参数会静默取 `null` 跳过整条链路）；
4. `DiCompositionTests` 用真实容器断言「场景 VM 拿到的 hub 就是容器里的 hub」。

### 8.5 性能约束（NFR-13，回归门禁）

帧内禁止堆分配，实测口径见 `docs/m2-perf-report.md`：

- 自有链路（插值合成 + 标定 + VM 刷新）**0 B**；纹理/点云空帧 **0 B**；
- 整帧 **72 B** = `TranslateTransform3D` 三个分量各装箱一个 `double`（WPF 固定成本，应用层无法消除）；
- 热路径禁忌：`ConcurrentDictionary.Values`、`ToList()`、字符串操作、LINQ——`PerfProbeTests` 会让它们变红。

---

## 9. M3 架构增量（预测分析与工艺辅助）

### 9.1 边界：新增两个命名空间，不新增类库

M3 的全部新增能力落在 `AoiTwin.Core` 内部的两个命名空间：

| 命名空间 | 内容 | 为什么放在 Core |
|---|---|---|
| `Core/Persistence` | 时序记录 / `ITimeSeriesStore` / 文件实现 / 批量 Sink / 录制回灌 / 存储治理 / 历史回放 / 健康档案 | 只依赖 `ITwinMessage` 与 Hub，无 UI 依赖——这正是「三期换机台原样复用」的那一层 |
| `Core/Analytics` | 预警契约 / 分析上下文 / 预测器注册表 / 调度器 / 健康评分 / 插件加载 / 导出 | 同上；另有结构测试断言它不引用 Rendering / WPF |

没有新开 `AoiTwin.Analytics` 类库，是一次**有意的取舍**：分析与存储的边界已经在接口上
（`ITimeSeriesStore` / `IAlertPredictor` / `IAnalyticsContext`），再拆程序集只会给 DI 装配和
「插件加载时不该看见渲染类型」这条约束多加一层间接，收益不明显。若将来分析层要独立部署（服务化），
把 `Core/Analytics` 整目录搬成新工程即可——依赖方向本来就是单向的。

依赖方向不变（结构断言见 `ExtensibilityTests`）：

```
AoiTwin.App ──▶ Rendering / Adapters / ViewModel ──▶ AoiTwin.Core
                                                       ├─ Bus / Recording / Catalog / Replay（M1）
                                                       ├─ Persistence（M3）
                                                       └─ Analytics（M3）
```

### 9.2 写入链路：Hub 的第二条消费支路

```
RealtimeHub ──Subscribe(5 Topic)──▶ PersistenceSink.Enqueue（只入队，零 IO）
                                        │
                                        ├─ 缓冲超 MaxBufferRecords → 丢最旧 + 计数
                                        └─ Timer 5s 或满 5000 条 ──▶ ITimeSeriesStore.Append(批)
                                                                       └─ <root>/<yyyyMMdd>.jsonl
                                                                          （UTC 天分区 + flushToDisk）
```

| 约束 | 做法 | 理由 |
|---|---|---|
| 绝不反压实时链路（NFR-22） | `Enqueue` 只入队，IO 在定时器线程 | 磁盘慢/满时丢掉的是历史，不是画面 |
| 失败可重试且有限 | 失败批次进重试队列，超 `RetryWindow`（默认 30 min）丢弃并计数 | 现场 U 盘拔插、网络盘抖动是常态 |
| 异常不扩散 | 存储异常只累加 `PersistenceStats.Failed`，`SafeFlush` 整体兜住 | 分析层的故障不能升级成设备停机 |
| 可观测 | `PersistenceStats.Healthy` = 有写入 + 无失败 + 缓冲未超限 | 健康页与长稳报告直接读这一个布尔 |

**ADR-M3-01：存储格式由「SQLite 按天分区」改为「JSONL 按天分区」。**
理由：零原生依赖（CI 与离线机免装）、现场可用记事本直接查、删过期数据等于删文件。
代价是区间查询退化为顺序扫描——30 天量级靠分区裁剪兜住（2160 条全量扫描 560 ms）。
`ITimeSeriesStore` 已抽象：若真机查询延迟超 NFR-21，换 SQLite 实现即可，上层零改动。

### 9.3 分析链路：只读时序库 + 健康档案 → 预测 → 回流 3D

```
ITimeSeriesStore.Query(窗口)  ┐
PartHealthAggregator.Snapshot()├─▶ AnalyticsContext ──▶ PredictorRegistry
AnalyticsOptions（阈值/权重）  ┘        （实现 IAnalyticsContext）      │ 逐个 try/catch 隔离
                                                                      ▼
                          TwinTopic.Alert  ◀── AlertScheduler ◀── IReadOnlyList<AlertFinding>
                                 │              （去重 / 升级即发 / 消失发 Info）
                                 ├──▶ AlertStateFx（3D 状态色 + 呼吸，纯增量接 M2 FX 框架）
                                 └──▶ AnalyticsExportService（CSV/JSON + 脱敏 + ACL）
```

三个模型的口径（**全部是真机必须复核的部分**）：

| 模型 | 观测量 | 判据 | 已知口径风险 |
|---|---|---|---|
| 光源衰减 | 通道亮度随时间的线性回归 | 斜率 < 0 才预测；`剩余天数 = (阈值 − 当前)/斜率`，并给出 R² 与 95% 区间 | **额定值必须配置**（`LightRatedValue`）；缺省会回落到窗口首样本，衰减会被自身一起抬高 |
| 轴磨损 | 保持段内位置离散度（z 分数）+ 漂移 µm/天 | 相对基线前半段超 σ 带 | 总线只有位置/速度，没有指令/反馈对——这是**代理量**，需真机用 PLC 双值交叉验证 |
| 节拍劣化 | 单站节拍趋势（Welford 单遍均值） | 超窗对比 + 百分比阈值 | 站点靠 `WaferId` 前缀解析；设备命名不含站点时会全归 `ALL`，瓶颈分析失效 |

**两条让模型可测的纪律**（否则回测永远不稳定）：

1. **时间只能来自上下文**：预测器一律用 `IAnalyticsContext.Now`，代码里禁止 `DateTime.Now`
   ——否则「提前 7 天报出」这类断言会随运行时刻漂移。
2. **预警必须能退色**：模型不出结论时，调度器要发一条 `Severity.Info` 的恢复事件；
   否则 3D 会永远停在红色——操作员很快就会学会无视它。

### 9.4 契约增量（只追加，向后读兼容）

| 契约 | 变更 | 兼容性 |
|---|---|---|
| `TwinTopic` | 追加 `Alert = 4` | 基值 0–3 不变；`ContractTests` 由「断言只有四个」改为「断言基值不变 + 无空洞追加」 |
| `MessageType`（帧 typeId） | 追加 `AlertEvent = 5` | 时序库记录借同一 typeId 空间标记 payload 类型 |
| `.aotrec` 文件格式 | **不变** | `CollectionSession` 只订阅 Motion/Status/Optics/Result 四类，分析事件从不进录制文件 |
| 新增消息 | `AlertEvent`（PartId/Kind/Severity/Score/Summary/Factors/HorizonDays/Confidence/Timestamp） | 走 `TwinJson` 同一序列化约定 |

`AlertEvent` 刻意**不含 Topic 字段**（`ITwinMessage` 只要求 `PartId`/`Timestamp`，Topic 在 Hub 上按订阅通道承载），
但它带 `DedupeKey = "{PartId}|{Kind}"`——**不含时间戳**。这一点是设计红线：含时间戳的话，
调度器每 5 分钟都会把它当成新预警，3D 提示会被反复重推。

### 9.5 渲染侧增量的边界纪律

| 增量 | 纪律 |
|---|---|
| LOD0 特写档 | **opt-in 能力**：`LodLevelSelector(thresholds, supportsClose)`，资产缺失时 `supportsClose=false`，行为与 M2 两档逐位一致——M1/M2 的回归用例因此完全不受影响 |
| 静态合批 | 只合**静态**几何，按（运动组，颜色）分组；法线与 UV 原样保留；按组独立 A/B，可单组回退 |
| GPU 上传分帧 | 4 MB/帧预算 + 优先级 + 同 Key 覆盖；超大条目独占一帧（否则它永远排不上） |
| 缺陷热力层 | 密度分 8 档合并 → Draw Call ≤ 8；可整体开关，关闭时不进渲染树 |
| UI3D KPI | 投影是纯数学（`WorldToScreenProjector` 可无头测试），相机后方返回 null；1s 节流 |
| 预警 FX | 复用 M2 的 `FxOverlay` 句柄语义：被同键替换后旧句柄变 no-op |

### 9.6 扩展点（M3 之后新增能力只需动这两处）

| 扩展 | 做法 | 不改的地方 |
|---|---|---|
| 新预测模型 | 实现 `IAlertPredictor`，`PredictorRegistry.Register` 即生效；外部 DLL 走 `PluginLoader` | Hub、渲染层、调度器 |
| 新数据源 | 复用 `StatusEvent` 的自定义 `EventCode`（示例：`VIBRATION_RMS`）或新增适配器 | 分析层（只要目标量能被 `IAnalyticsContext` 读到） |
| 新机台 | 台账 JSON + GLB + `ITransport` 实现 | 以上全部 |

插件加载的安全默认：`PluginLoadOptions.AllowedPublicKeyTokens` **为空即全拒**。
现场要加载自定义 DLL，必须先配置公司公钥 token——这条在 `m3-plugin-guide.md` 里有完整流程。
