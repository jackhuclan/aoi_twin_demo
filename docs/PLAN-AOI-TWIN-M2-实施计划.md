# 实施计划 · AOI 数字孪生二期（M2）：实时数据映射与状态同步

| 项 | 内容 |
|---|---|
| 文档编号 | PLAN-AOI-TWIN-M2 |
| 版本 / 状态 | v1.1 / 执行中（TA.0 起实施） |
| 上游文档 | PRD-AOI-TWIN-M2 · PLAN-AOI-TWIN-M1（承接其 §6 分工与提交约定） |
| 工作方法 | 同 M1：Core/ViewModel 层红绿 TDD + 渲染层验证清单 + 回放数据驱动 + 真机窗口两次 |
| 计时基准 | T = M1 验收签字日 |

---

## 0. 目标与范围（先对齐，再动手）

### 0.1 M2 要解决的一句话

M1 交付的是"静态的壳 + 数据的路"；M2 要让它变成**实时镜像**：
设备位姿 → Hub（节流）→ VisualViewModel（副本 + 上传队列）→ 渲染线程 60fps 插值采样 → 画面。

### 0.2 范围边界

| 做 | 不做（→ M3） |
|---|---|
| 节流分发正式版、VisualViewModel 层、运动/光学/晶圆三条数据链路 | 时序持久化、健康度、预测模型 |
| 点选钻取、缺陷 2D/3D 双向联动、跟随模式 | LOD 第三档（特写 PBR）、静态合批终调 |
| FX 按需渲染框架 + 首批三特效（描边/报警脉冲/粒子） | KPI 标注内容（只前移投影骨架） |
| 回放数据自动回归（进 CI） | 72h 之外的长期档案 |

### 0.3 与 M1 的三条衔接约束

1. **契约只增不改**：`IRealtimeHub` 原 `Subscribe<T>(topic, handler)` 签名保留，新增带 `ThrottlePolicy` 的重载；
2. **场景装配树不变**：`SceneRoot → 部件节点(Transform) → Model3DGroup → GeometryModel3D(PartId)`，M2 只在部件节点上叠运动量，不重建树；
3. **点选集合仍固定 L1 几何**：M2 的 FX 与联动都挂在 PartId 上，不碰拾取实现。

---

## 1. 关键冲突与决策（动手前必须落定）

| # | 冲突 | 决策 | 理由 |
|---|---|---|---|
| D-1 | **部件命名不一致**：M1 台账是 `GATE_AXIS_X/Y`、`WAFER_STAGE`、`CAMERA_UNIT`；PRD M2 §5.3 与已下发的 `assets/mock-dataset/mock-scenario.json` 用的是 `ROBOT`、`STAGE` | **扩展台账到 M2 部件集**（保留可复用件，新增 8 个），占位 GLB 由 `AssetTools gen-placeholder` 一并生成 | ①数据侧已经按 ROBOT/STAGE 下发，改名比永久加映射层便宜；②台账是唯一权威来源，运动学按 `axes` 驱动、不写死代码；③真实资产到位只换 GLB |
| D-2 | **WPF 3D 无 InstancedMesh / BufferAttribute**（PRD 用了 Three.js 词汇） | 缺陷点云用**分块冻结 + 追加**：按缺陷等级分池，每 512 点生成一块新几何并 Freeze | WPF 的 `MeshGeometry3D` 参与渲染后修改会触发整块重建；分块追加使每帧工作量 ∝ 新增点，O(增量) 成立 |
| D-3 | **WPF 无 GPU 纹理上传 API** | Wafer Map 用 `ImageBrush(BitmapSource)` 挂在 `DiffuseMaterial` 上，整片刷新=换 Material 的 Brush，几何与变换不动 | 满足 FR-M2-06「不重建几何体」；`WriteableBitmap` 可原地改像素，连 Brush 都不用换 |
| D-4 | **无 stencil buffer** | 选中描边复用 M1 反向壳方案（`SelectionOutlineFx`），报警脉冲用外发光材质强度动画 | M1 已验证；不引入新依赖 |
| D-5 | **热路径禁堆分配（NFR-13）** | 插值/采样/节流合并一律 `struct` + 预分配缓冲；订阅回调不捕获闭包；提供帧内分配断言用例 | 否则 60fps 下 GC 抖动不可控 |
| D-6 | **轴数据 2–5Hz 抖动** | 线性插值 + 单调时间戳防回跳；渲染线程按 `renderTime` 采样，不追最新值 | Catmull-Rom 会过冲，机械运动宁可"略滞后"也不要反向超调 |

---

## 2. 任务分解（含实现方案 / 依赖 / 优先级 / 环境）

> 优先级：P0 = 验收必过（PRD §7-1）；P1 = 可顺延至三期初。
> 环境：🖥 可离线完成（含回放数据驱动）｜🏭 需真机窗口。

### S1 数据管线升级（TA.0–TA.4）

| 任务 | 优先级 | 环境 | 实现方案（关键类 / 文件） | 依赖 | 验证判据 |
|---|---|---|---|---|---|
| **TA.0** 契约承接确认 | P0 | 🖥 | `docs/m2-contract-baseline.md`：核对 M1 冻结契约 + 本期新增（`ThrottlePolicy`、三个 VM 接口、`IFxOverlay`、`IPartDetailDrillDown`） | M1 | 基线 CI 全绿 |
| **TA.0A** 资产与台账扩展（D-1） | P0 | 🖥 | `AssetTools gen-placeholder` 增 8 个 M2 部件（`ROBOT`/`STAGE`/`PRE_ALIGNER`/`GATE_AXIS_Z`/`CAMERA_LINE_SCAN`/`LIGHT_COAX`/`WAFER_CURRENT`/`TRACK`）；`part-catalog.json` 补 `axes` 声明；`mock-scenario.json` 补全 9 轴 | TA.0 | `AssetTools verify` 通过；台账 18 件、9 轴全覆盖 |
| **TA.1** 节流分发 | P0 | 🖥 | `Core/Bus/ThrottlePolicy.cs`（`record struct`）、`RealtimeHub.Subscribe<T>(topic, handler, policy)`；合并窗口用「每订阅者一个 `lastEmit` 时间戳 + 保最新值字段」而非定时器（无额外线程）；`ThrottlePolicyStore` 走 `FileSystemWatcher` + 原子替换热加载 | TA.0 | TDD：200ms 窗口内 N 条只见到 1 条且是最后一条；`TimeSpan.Zero` 逐条直推；**帧内零分配**；热加载坏配置保留旧值 |
| **TA.2** VisualViewModel 层 | P0 | 🖥 | 新增工程 `src/AoiTwin.ViewModel`（net10.0，无 UI 依赖）：`IMotionPartViewModel` 双缓冲（两个 `PoseBuffer` 轮换 + `Interlocked` 交换，写者不阻塞读者）、`OpticsPartViewModel`、`WaferDataViewModel`（上传队列 `ConcurrentQueue<PendingOp>`）、`TwinVisualViewModel`（Hub 订阅聚合 + 异常边界） | TA.1 | TDD：数据线程写/渲染线程读 10 万次无撕裂无异常；订阅回调异常被 VM 边界吞掉并置降级标志；上传队列不丢不重 |
| **TA.3** 坐标换算与插值 | P0 | 🖥 | `Core/Kinematics/TeachCoordinateMapper`（台账 `axes` + 标定系数 JSON → 每轴 mm→m 缩放/偏置/符号）、`TransformInterpolator`（每轴环形样本缓冲，线性插值，时间戳倒退则丢弃） | TA.2 | TDD：单调时间戳防回跳；`Sample(t)` 在两端点严格等于端点值；插值连续性（相邻帧位移 < 阈值）；标定文件缺失降级为恒等映射 |
| **TA.4** 四部件运动同步 | P0 | 🖥 | `Rendering/Dynamics/MotionSyncDriver`：挂 `Viewport.FrameRendered` → 遍历 `PartVisual` → `Sample(now)` → 写 `TranslateTransform3D.Offset*`；LOD2 合批节点按运动组取组内首件位姿 | TA.3 | 回放驱动四部件运动无可见抖动；虚拟/实际偏差 ≤ 1 个轴数据周期；LOD1/LOD2 两档均动 |

**S1 退出**：回放数据驱动四部件运动无抖动（PRD MS-M2-1）。

### S2 状态与晶圆（TA.5–TA.9）

| 任务 | 优先级 | 环境 | 实现方案 | 依赖 | 验证判据 |
|---|---|---|---|---|---|
| **TA.5** 光学状态映射 | P0 | 🖥 | `LightChannelPalette`（PRD §5.5 配置 schema，8 通道→色）；`OpticsPartViewModel` 收到 `OpticsEvent` 改写材质参数副本；渲染帧把色值写到 `DiffuseMaterial.Brush`（`LIGHT_RING` 按通道分区网格，逐区改材质）；`LIGHT_COAX` 亮度→`EmissiveMaterial` 强度 | TA.2 | 切通道到变色 ≤ 100ms；8 通道色与配置一致；配置热加载 |
| **TA.6** 视野锥与扫描进度 | P0 | 🖥 | `Rendering/Dynamics/ScanFrustumVisual`：预建锥体/棱台几何（一次），只改 Transform；进度线用 `LinesVisual3D` 两点改 `Points`（或独立 `LineGeometry`）随 `ScanProgress`（500ms）推进 | TA.5 | 锥体覆盖扫描区域（与 Wafer 尺寸对齐断言）；进度线 500ms 推进 |
| **TA.7** Wafer Map 纹理 | P0 | 🖥 | `WriteableBitmap`（die 尺寸 → 像素）+ `ImageBrush`；`Wafer_Current` 平面网格一次建好，整片刷新只 `WritePixels` | TA.2 | 整片刷新掉帧 < 5fps；不重建几何（断言 `Geometry` 引用不变）；二值/灰度均正确 |
| **TA.8** 缺陷点云增量 | P0 | 🖥 | 按等级分池的 `PointsVisual3D` 分块（512 点/块，块满新开）；高亮用 8 顶点独立小面片跟随目标点 | TA.7 | 追加 1 万点无全量重建（块数 = ⌈N/512⌉）；按等级着色；O(增量) 探针 |
| **TA.9** 真机联调窗口 A | P0 | 🏭 | 现场标定系数入库 + 三链路演示 | TA.4–TA.8 | 现场演示通过；附带 24h 压测 |

### S3 联动与特效（TA.10–TA.16）

| 任务 | 优先级 | 环境 | 实现方案 | 依赖 | 验证判据 |
|---|---|---|---|---|---|
| **TA.10** FX 按需渲染框架 | P0 | 🖥 | `Rendering/Fx/FxOverlay`：`OverlayRoot` 下按 PartId 挂子节点；`Attach` 返回句柄，`Dispose` 摘除；**无 FX 时 OverlayRoot 零子节点 → 不进 Draw Call**；枚举追加不改名（M3 预留） | TA.2 | 特效开关不影响基线帧率；空 FX 时 OverlayRoot.Children.Count == 0 |
| **TA.11** 首批特效 | P0 | 🖥 | 描边=复用 `SelectionOutlineFx`；报警脉冲=外发光材质强度 0→1 呼吸（`DispatcherTimer` 16ms，不用 Storyboard 以便随时摘除）；传输粒子=沿指定线段的 `PointsVisual3D` 循环推进 | TA.10 | 脉冲与 `StatusEvent(Alarm)` 同步触发/解除；三特效可独立开关 |
| **TA.12** 部件钻取联动 | P0 | 🖥 | `Panel/PartDrillDownService`：`Open(partId)` 建临时 Hub 订阅（Status/Optics 过滤 PartId）→ 右侧面板切详情（IO 状态/寿命计数/最近报警）；`Close()` 释放订阅 | TA.1 | 切换 ≤ 200ms；取消选中订阅数回到基线（无泄漏用例） |
| **TA.13** 右键诊断跳转 | P1 | 🖥 | 视口右键菜单按 `台账 module` 映射到 2D 页签（OPTICS→相机/光源、MOTION→轴调试） | TA.12 | 按部件类型跳转正确页 |
| **TA.14** 缺陷双向联动 | P0 | 🖥 | `DefectIndex`（DefectId → 池/块/索引）；2D 列表选中 → 高亮面片定位（≤100ms）；3D 点选高亮面片 → 反查 DefectId → 2D 列表滚动定位 → 原图窗口 | TA.8 | 四条链路演示；原图窗口与 DefectId 正确关联 |
| **TA.15** 跟随模式 | P1 | 🖥 | `Rendering/Camera/FollowCamera`：每帧取目标部件世界坐标，Target 一阶低通（α≈0.2）跟随，Position 保持相对偏移；解除恢复自由视角 | TA.4 | 追踪平滑（相邻帧位移连续、无跳变）；可随时解除 |
| **TA.16** 占位/装载状态 | P1 | 🖥 | `StatusEvent`(Track/LoadPort) → `TRACK` 晶圆显隐、`LOAD_PORT` Cassette 计数显示（1s 节流） | TA.10 | 状态变化与事件一致 |

### S4 长跑与验收（TA.17–TA.20）

| 任务 | 优先级 | 环境 | 实现方案 | 依赖 | 验证判据 |
|---|---|---|---|---|---|
| **TA.17** 回放回归模式 | P1 | 🖥 | `TwinVisualRegression`（`tests/AoiTwin.Regression.Tests`，独立进程）：用 .aotrec 合成录制驱动 TA.4–TA.8，断言「帧数一致 / 末帧位姿接近期望 / 上传队列消费完毕」 | TA.4–TA.8 | 回归套件进 CI 全绿 |
| **TA.18** 真机窗口 B + 72h 长跑 | P0 | 🏭 | ✅ 离线部分：`soak-visual` 模式（带 VM/渲染循环/FX，退出码 + 心跳 + CSV）、`tools/soak-visual.cmd`、`docs/m2-stability-report.md`（判据/流程/判读规则 + 30 分钟自检）；🏭 待办：窗口 B 就地跑 72h 并回填报告 | 全部 | 无崩溃/增长 <100MB/帧率衰减 <5% |
| **TA.19** 性能报告 | P0 | 🖥/🏭 | `docs/m2-perf-report.md`：NFR-11/12/13/17 逐条；帧内分配用 `GC.GetAllocatedBytesForCurrentThread` 断言 | TA.18 | 报告达标 |
| **TA.20** 验收与文档 | P0 | 🖥 | `docs/m2-acceptance-checklist.md`；README/PRD/白皮书同步；对 M3 冻结清单 | TA.19 | 评审签字 |

---

## 3. 依赖图与执行顺序

```
TA.0 契约基线
  └─TA.0A 资产/台账扩展
        └─TA.1 节流 ──┬─TA.2 VisualViewModel ─┬─TA.3 坐标/插值 → TA.4 运动同步 ─┬─TA.9 窗口A(🏭)
                      │                        ├─TA.5 光学 → TA.6 视野锥    │
                      │                        ├─TA.7 WaferMap → TA.8 点云 →─┴─TA.14 缺陷联动
                      │                        ├─TA.10 FX ─┬─TA.11 首批特效
                      │                        │           ├─TA.16 占位/装载
                      │                        └─TA.12 钻取 ─ TA.13 右键跳转
                      └──────────────────────── TA.17 回放回归 ← TA.4/5/6/7/8
TA.15 跟随 ← TA.4                       TA.18 长跑 ← 全部 → TA.19 性能 → TA.20 验收
```

**串行主干（本次按此顺序实现）**：
`TA.0 → TA.0A → TA.1 → TA.2 → TA.3 → TA.4`（S1 可离线收口）
→ `TA.5 → TA.6 → TA.7 → TA.8`（S2 代码部分）
→ `TA.10 → TA.11 → TA.12 → TA.14`（S3 P0）
→ `TA.15 → TA.13 → TA.16 → TA.17`（S3 P1 + 回归）
→ `🏭 TA.9 / TA.18`（真机窗口，需现场）→ `TA.19 → TA.20`

---

## 4. 交付物清单

- **代码**：`AoiTwin.ViewModel`（新增工程）、`Core/Bus/ThrottlePolicy.cs`、`Core/Kinematics/*`、`Rendering/Dynamics/*`、`Rendering/Fx/*`、`Panel/PartDrillDownService`；
- **资产**：台账扩到 M2 部件集 + 占位 GLB；`lightChannelColors` / `stateColors` / `defectClassColors` 配置；坐标标定文件；
- **报告**：`m2-contract-baseline.md`、`m2-stability-report.md`（72h）、`m2-perf-report.md`、`m2-acceptance-checklist.md`；
- **CI**：回放回归套件入门槛。

## 5. 与 M3 的协调节点（沿用 v1.0，未变）

| # | 节点 | 时间 | 内容 |
|---|---|---|---|
| ① | `PartFxKind` 枚举预留 | TA.10 | M3 追加 `AlertState/DefectHeatmap/KpiBadge`：只追加值，不改渲染主循环 |
| ② | M2→M3 契约冻结 | TA.20 | VM 契约、上传队列、钻取框架、颜色 schema（`stateColors` 预留 `Warning/Critical`） |
| ③ | 时序数据沉淀提前 | TA.18 | M3 的持久化 Sink 提前 2 周并行，随 72h 长跑部署 |
| ④ | UI3D 浮层骨架前移 | TA.10 | 世界坐标→屏幕投影 + Adorner + 1s 节流 |
| ⑤ | 真机窗口衔接 | TA.18 | 窗口 B 同步部署 Sink |
| ⑥ | 包升级窗口 | M2 验收后 | HelixToolkit 等一次性升级，M2 期间锁版本 |

## 6. 风险与应对

| 风险 | 触发信号 | 应对 |
|---|---|---|
| R-1 时序数据不足致 M3 回测延期 | TA.18 未部署 Sink | 节点 ③：Sink 提前并行 + `.aotrec → 时序库` 回灌工具兜底 |
| R-2 轴坐标标定失败 | 窗口 A 偏差超限 | TA.3 换算独立成模块，W1 即用录制数据预标定，现场仅微调 |
| R-3 长跑泄漏/帧率衰减 | 24h 压测超阈 | 每次联调附 24h 压测；`GC.GetAllocatedBytesForCurrentThread` 抽查热路径 |
| R-4 联动复杂度超预期 | TA.14 延期 | 统一 `PartId`/`DefectId` 寻址服务，禁止各链路私连 |
| R-5 WPF 无 InstancedMesh 致点云变慢 | TA.8 追加 1 万点掉帧 | D-2 分块冻结；超限降级为「只画最新 N 点 + 计数」 |

## 7. 执行进度

| 任务 | 状态 | 提交 / 版本 |
|---|---|---|
| TA.0 契约基线 | ✅ | `docs/m2-contract-baseline.md`（0.2.0） |
| TA.0A 资产扩展 | ✅ | `4cbb38b` |
| TA.1 节流分发 | ✅ | `d38cdfd` |
| TA.2 VisualViewModel | ✅ | `23b6282` |
| TA.3 坐标与插值 | ✅ | `d7e622e` |
| TA.4 运动同步 | ✅ | `263ea47` |
| TA.5–TA.8 状态与晶圆 | ✅ | `e598990` / `e26cae4` / `58c2401` / `d164365` |
| TA.10–TA.11 FX 与首批特效 | ✅ | `87fc282` |
| TA.12 部件钻取 | ✅ | `c2e43b2` |
| TA.13 右键诊断跳转 | ✅ | `fd27e94` |
| TA.14 缺陷双向联动 | ✅ | 本阶段提交 |
| TA.15 跟随模式 | ✅ | `092c994` |
| TA.16 占位/装载状态 | ✅ | 本阶段提交 |
| TA.17 回放回归 | ✅ | 本阶段提交（独立工程 `tests/AoiTwin.Regression.Tests`） |
| TA.9 真机窗口 A | 🏭 待现场 | 标定系数入库 + 三链路演示（链路与文件格式已就绪） |
| TA.18 长跑（工具与脚本） | ✅ | `tools/AoiTwin.Soak soak-visual`（VM + 渲染循环 + FX）、`tools/soak-visual.cmd`、`docs/m2-stability-report.md`（离线 30 分钟自检 + 判读规则） |
| TA.18 长跑（72h 真机） | 🏭 待窗口 B | 就地跑 `tools\soak-visual.cmd`，报告回填 `docs/m2-stability-report.md` §4/§8 |
| TA.19 性能报告（离线探针 + 报告） | 🟡 | `docs/m2-perf-report.md`（离线 7 探针全绿；真机/核显帧率与长跑待 TA.18） |
| TA.20 验收与文档 | ✅ | `docs/m2-acceptance-checklist.md`、README/PRD/白皮书同步、M3 冻结清单（`609a326`） |
