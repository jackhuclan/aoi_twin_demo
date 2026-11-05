# M2 验收清单（TA.20）

> 依据：`docs/PRD/PRD-AOI-TWIN-M2-实时数据映射与状态同步.md` §7 验收标准（DoD）七项
> 与 §5/§6 的 FR-M2-01~14、NFR-11~18。
> 执行方式：逐条对照实现与自动化证据，人工签署栏留空待评审会签字。
> 状态图例：✅ 已满足 ｜ 🟡 部分满足/待现场条件 ｜ ⬜ 未满足

---

## 0. 结论速览

| # | PRD §7 验收项 | 状态 | 阻塞项 |
|---|---|---|---|
| 1 | 功能：FR-M2-01~08、10、12（P0）通过；P1 完成或顺延 | ✅ | 无（P1 十三条全部完成，无顺延） |
| 2 | 同步精度：四轴插值无可见抖动；虚拟/实际偏差 ≤ 1 个轴数据周期 | 🟡 | 抖动已用回放回归验证；**偏差标定需真机**（TA.9 窗口 A） |
| 3 | 联动：四条链路（钻取/右键诊断/缺陷双向/跟随）演示通过 | ✅ | 无（自动化端到端已覆盖，见 §1.3） |
| 4 | 性能：NFR-11~13、17 达标（真机 + 核显两档） | 🟡 | 离线部分达标并出报告；真机/核显帧率待 TA.18 |
| 5 | 长跑：72h 真机连续运行报告（NFR-14） | 🟡 | 待 TA.18 现场窗口 B |
| 6 | 回归：回放套件覆盖 FR-M2-03~07 并接入 CI | ✅ | 无（独立工程，CI 每次执行） |
| 7 | 两个真机联调窗口完成，示教标定文件入库 | 🟡 | 待现场；标定文件格式与加载链路已就绪（`assets/teach-calibration.json`） |

**总体判定**：代码与自动化部分全部收口（255 例全绿、0 警告 0 错误、CI success）；
剩余 🟡 全部是**现场条件依赖**，不是实现缺口。逐条说明见下。

---

## 1. 功能验收（PRD §7-1）

### 1.1 P0 功能项（FR-M2-01 ~ 08、10、12）

| 编号 | 功能 | 实现位置 | 自动化证据 | 状态 |
|---|---|---|---|---|
| FR-M2-01 | 节流分发正式版（200ms 按键合并、事件直推、配置文件热加载） | `Core/Bus/ThrottlePolicy`、`RealtimeHub`（`ThrottledSink`）、`ThrottlePolicyFileSource` | `ThrottlePolicyTests`（8 例：按键合并/保留最新/坏配置保旧值/热加载/订阅方隔离） | ✅ |
| FR-M2-02 | VisualViewModel 层（与数据线程零共享可变状态） | `AoiTwin.ViewModel/TwinVisualViewModel`（net10.0 纯数据，不依赖 WindowsBase） | `TwinVisualViewModelTests`（9 例） | ✅ |
| FR-M2-03 | 运动同步（示教换算 + 2–5Hz → 60fps 插值） | `Core/Kinematics/TeachCoordinateMapper`、`AxisInterpolator`；`ViewModel/MotionPoseEngine`；`Rendering/Dynamics/MotionSyncDriver` | `KinematicsTests`（10）、`MotionPoseEngineTests`（8）、`UiSmokeTests` 运动段、`TwinVisualRegressionTests` | ✅ |
| FR-M2-04 | 光源通道变色（8 通道映射表可配置） | `Rendering/Dynamics/OpticsSyncDriver`；`ViewModel/LightChannelPalette(FileSource)` | `OpticsSyncDriverTests`（4）、`LightChannelPaletteTests`（6） | ✅ |
| FR-M2-05 | 视野锥与扫描进度 | `Rendering/Dynamics/ScanFrustumDriver` | `ScanFrustumDriverTests`（3） | ✅ |
| FR-M2-06 | Wafer Map 纹理整片替换（不重建几何） | `Rendering/Dynamics/WaferMapDriver`；`ViewModel/WaferUploadQueue` | `WaferMapDriverTests`（4：整片替换/引用不变/几何零重建） | ✅ |
| FR-M2-07 | 缺陷点云增量（O(增量)、按等级着色） | `Rendering/Dynamics/DefectPointDriver` | `DefectPointDriverTests`（7：分块/等级配色/1 万点/高亮/反查） | ✅ |
| FR-M2-08 | 部件钻取面板联动 | `App/Services/PartDrillDownService`、`ViewModels/DrillDownViewModel` | `PartDrillDownTests`（3：打开填充/切换 ≤200ms/关闭无泄漏） | ✅ |
| FR-M2-10 | 缺陷 2D/3D 双向联动 | `Rendering/Dynamics/DefectIndexEntry`+`IDefectPickSource`；`ViewModel/DefectListViewModel`；`App/Services/DefectLinkService`、`Views/DefectImageWindow` | `DefectLinkTests`（8：四链路 + 高亮 ≤100ms + 滑窗定位）、`DefectListViewModelTests`（6） | ✅ |
| FR-M2-12 | FX 按需渲染框架 + 首批特效 | `Rendering/Fx/FxOverlay`、`AlarmPulseFx`、`TransportParticlesFx` | `FxOverlayTests`（4：空 FX 零节点/同键替换/旧句柄不误删/摘除退出叠加层） | ✅ |

### 1.2 P1 功能项（FR-M2-09、11、13、14）

| 编号 | 功能 | 实现位置 | 自动化证据 | 状态 |
|---|---|---|---|---|
| FR-M2-09 | 右键跳转 2D 诊断页（按类型分流） | `Viewport/TwinViewport3D.SceneRightClicked`；`ViewModels/TwinSceneViewModel.OpenDiagnostic` | `UiSmokeTests`（四类分流断言：相机/光源/轴/通用） | ✅ |
| FR-M2-11 | 跟随模式（锁定载台，解除恢复） | `Rendering/Camera/FollowCamera` | `FollowCameraTests`（3：平滑系数/相对偏移保持/停止后不再追踪） | ✅ |
| FR-M2-13 | 晶圆占位与装载状态（Track + Cassette 25 片） | `ViewModel/HandlingStateViewModel`；`Rendering/Dynamics/HandlingSyncDriver` | `HandlingSyncDriverTests`（4）、`HandlingStateViewModelTests`（9） | ✅ |
| FR-M2-14 | 回放回归测试模式 | `tests/AoiTwin.Regression.Tests/TwinVisualRegression` | `TwinVisualRegressionTests`（1 例，断言 36 帧/末帧位姿/队列 12 进 12 消 0 挤压/24 点/8 通道） | ✅ |

### 1.3 四条联动链路（PRD §7-3）

| 链路 | 触发方式 | 端到端证据 |
|---|---|---|
| 部件钻取 | 左键点选部件 → 右栏「部件详情」 | `PartDrillDownTests` 打开即填充 IO/寿命/报警，切换 ≤200ms |
| 右键诊断 | 右键部件 → 自动切到「诊断」页签 | `UiSmokeTests` 四类分类 + 页签索引断言 |
| 缺陷双向 | 列表选中 ↔ 3D 高亮 ↔ 原图窗口 | `DefectLinkTests` 四条方向全覆盖，高亮 ≤100ms |
| 跟随模式 | 页头「跟随载台」开关 | `FollowCameraTests` + UI 冒烟（开关状态与相机动效） |

---

## 2. 同步精度（PRD §7-2）

| 判据 | 证据 | 状态 |
|---|---|---|
| 四轴运动插值无可见抖动 | `AxisInterpolator` 环形 32 样本线性插值，端点上严格相等（`KinematicsTests` 断言）；`MotionPoseEngine` 时间戳倒退丢弃 | ✅ |
| 虚拟/实际偏差 ≤ 1 个轴数据周期 | 需要真机示教坐标与本机实际位置对照 | 🟡 待 TA.9 窗口 A |

**说明**：偏差标定的前置链路已经就绪——`assets/teach-calibration.json`（µm→m、方向、Scale/Sign/Offset）格式冻结，
`TeachCoordinateMapper` 缺键恒等降级（标定缺失不阻塞渲染）。现场只需填数并复核。

---

## 3. 性能（PRD §7-4）

详见 `docs/m2-perf-report.md`。摘要：

| 编号 | 判据 | 离线实测 | 状态 |
|---|---|---|---|
| NFR-11 | ≥ 60fps；整片刷新掉帧 < 5fps | 单帧 CPU 成本：整片 9 万 die 5.9ms（预算 36%）、1 万点缺陷切换 48ms（2.9 帧） | 🟡 GPU 侧待现场 |
| NFR-12 | 事件 ≤150ms / 轴 ≤300ms / 高亮 ≤100ms | 0.4ms / 216–225ms / 2.0ms | ✅ |
| NFR-13 | 帧内零堆分配 | 自有链路 0 B、空帧 0 B、整帧 72 B（WPF 装箱） | ✅ |
| NFR-17 | 核显 ≥30fps + 降级路径 | 全部测量跑在 Intel Iris Xe 上；空 FX 退出 Draw Call | 🟡 帧率待现场 |

---

## 4. 长跑（PRD §7-5）

| 项 | 状态 | 证据 |
|---|---|---|
| 长跑工具（带 VM 与渲染循环） | ✅ | `tools/AoiTwin.Soak soak-visual`（六路同步驱动器 + 报警/粒子 FX + 跟随相机，真实 GLB 装配，WPF 帧循环） |
| 作业脚本与判读规则 | ✅ | `tools/soak-visual.cmd`、`docs/m2-stability-report.md` §2/§6 |
| 离线 30 分钟全链路自检 | ✅ | 同报告 §4（证明工具与判据可用、曲线进平台，**不作 72h 结论**） |
| 72h 真机连跑 | 🟡 待 TA.18 窗口 B | 判据：无崩溃 / 增长 < 100MB / 帧率衰减 < 5%，结果回填 `docs/m2-stability-report.md` §4 与 §8 签字 |

**已知前置风险**：缺陷点云无保留策略（按 `DefectId` 永久累加）。长跑报告已单列 `defectPoints`
用于归因——若 100MB 被点云顶破，属设计缺口而非泄漏，处置见 `docs/m2-stability-report.md` §7-1。

---

## 5. 回归（PRD §7-6）

`tests/AoiTwin.Regression.Tests`（独立进程）用合成 `.aotrec` 经 `ReplayEngine` 真回放驱动 FR-M2-03~07 四个驱动器：

- 帧数一致（12 片 × 3 主题 = 36 帧）；
- 末帧位姿 1 100 000 µm → 1.1 m（插值 + 标定链路）；
- 上传队列 12 入队 / 12 消费 / 0 挤压；
- 缺陷点云 24 点、8 通道全部变色。

> 该工程**必须独立进程**：WPF 的 `Application.Current` 与 `MediaContext` 有线程归属，
> 与 App.Tests 同进程会互相拖死（本地实测冒烟 60s 超时）。

---

## 6. 真机窗口（PRD §7-7）

| 窗口 | 内容 | 前置条件 | 状态 |
|---|---|---|---|
| A（MS-M2-2） | 标定系数入库、三链路演示 | 设备可达、示教坐标可读 | 🟡 待排期 |
| B（MS-M2-4） | 72h 长跑、性能调优、验收评审 | 窗口 A 完成 | 🟡 待排期 |
| ↑ 窗口 B 长跑 | 就地跑 `tools\soak-visual.cmd`（72h） | 作业步骤与巡检表：`docs/m2-stability-report.md` §5 | 🟡 工具已就绪 |

现场作业指导与登记表：`docs/field-collection-runbook.md`、`assets/recordings/manifest.md`。

---

## 7. 工程门槛

| 项 | 实测 | 状态 |
|---|---|---|
| 构建 | `dotnet build AoiTwin.slnx -c Release` → 0 警告 0 错误 | ✅ |
| 测试 | 255 例全绿（Core 77 / Rendering 85 / ViewModel 42 / App 14 / Regression 1 / Adapters 23 / AssetTools 13） | ✅ |
| CI | GitHub Actions build + 全量测试，全绿 | ✅ |
| DI 装配一致性 | `DiCompositionTests` 用**真实容器**解析场景 VM，断言「用户看的就是容器里的 hub」且轴数据按 µm→m 标定落到 VM（锁死 M2 出现过的假接线） | ✅ |
| 契约 | M1 冻结契约只增不改（`docs/m2-contract-baseline.md` §1 逐条核对） | ✅ |

---

## 8. 顺延项与已知偏差

| # | 项 | 影响 | 处置 |
|---|---|---|---|
| 1 | 旋转轴（`R`/`THETA`）标定 `Direction=NONE`，回转不可见 | 占位资产期无轴心几何 | 待真实 GLB 资产；`PoseSnapshot` 已是 4×4，置换矩阵已预留 |
| 2 | 1 万点缺陷整片切换 48ms（掉 2.9 帧） | 整片切换瞬间轻微卡顿 | M3 引入每帧点数预算（4096 点/帧） |
| 3 | ~~缺陷等级配色为代码内默认表~~ | 已闭环：`assets/light-channel-colors.json` 扩为三表（通道 / 缺陷等级 / 状态），热加载生效 | — |
| 4 | 缺陷原图窗口为占位靶心图 | 联动链路完整、像素为示意 | 接入一期图像服务时补 NFR-16 ACL 走查 |
| 5 | 真机帧率/长跑/同步偏差 | 三项 DoD 判据依赖现场 | TA.18 窗口 A/B |

---

## 9. M3 冻结清单（提交评审）

M2 交付的以下契约在 M3 只增不改：

| 契约 | 位置 | 冻结理由 |
|---|---|---|
| `ITwinMessage` / `TwinTopic` / 四类消息 record | `AoiTwin.Core/Bus` | 数据侧与上位机的协议面，改一次全链路重编 |
| `IRealtimeHub`（含节流重载） | `AoiTwin.Core/Bus/IRealtimeHub` | 所有 ViewModel 的订阅入口 |
| `ThrottlePolicy`（MinInterval/KeepLatest/MergeKey） | `Core/Bus` | 配置文件 schema 已对现场公布 |
| `IMotionPoseSource` / `PoseSnapshot`（4×4 行主序） | `AoiTwin.ViewModel` | 运动学与渲染层的唯一接口；旋转轴接入不改签名 |
| `AxisCalibration`（PartId/AxisName/Direction/Scale/Sign/Offset） | `Core/Kinematics` | 示教标定文件格式，现场已按此填数 |
| `IDefectPickSource` / `DefectIndexEntry` | `Rendering/Dynamics` | 2D↔3D 联动的寻址契约（风险 R-4「禁止链路私连」的落地） |
| `IWaferDataViewModel`（有界 8 片 + 三计数） | `ViewModel/WaferUploadQueue` | 背压语义，M3 持久化 Sink 复用同一队列 |
| `LightChannelPalette` / `RgbColor` + 配色文件三表 schema | `ViewModel`、`assets/light-channel-colors.json` | PRD §5.5 颜色 schema：`lightChannelColors`（必需）/ `defectClassColors` / `stateColors`，现场改文件即生效 |
| `stateColors.Warning` / `Critical` 预留键 | `assets/light-channel-colors.json` + `LightChannelPalette.DefaultStateColors` | M3 报警分级只需取值，**不改 schema**（有用例锁定键存在性） |

---

## 签署

| 角色 | 姓名 | 日期 | 签字 |
|---|---|---|---|
| 产品 | | | |
| 研发 | | | |
| 测试 | | | |
| 现场工艺 | | | |
