# 实施计划 · AOI 数字孪生二期（M2）：实时数据映射与状态同步

| 项 | 内容 |
|---|---|
| 文档编号 | PLAN-AOI-TWIN-M2 |
| 版本 / 状态 | v1.0 / 待评审 |
| 上游文档 | PRD-AOI-TWIN-M2 · PLAN-AOI-TWIN-M1（承接其 §6 人力分配与提交约定） |
| 工作方法 | 同 M1：Core 层红绿 TDD + 渲染层验证清单 + 真机窗口两次 |
| 计时基准 | T = M1 验收签字日（PRD §8 里程碑映射） |

---

## 0. 前置依赖（进入 M2 的门槛）

1. M1 验收全部通过（PRD-AOI-TWIN-M1 §7 六项），`assets/recordings/` 有 ≥ 3 个真实班次可回放；
2. M1 冻结契约可用：`ITwinMessage` 四类消息、`IRealtimeHub`、录制文件格式、PartId 台账；
3. 人力三线沿用 M1 分工：A 图形/渲染、B 数据/总线、C 现场/工具。

---

## 1. 阶段划分与时间安排

| 阶段 | 周期 | 主题 | 对应 PRD 里程碑 |
|---|---|---|---|
| S1 数据管线升级 | T+0 ~ T+3 周 | 节流分发 + VisualViewModel 骨架 + 运动同步（回放数据驱动） | MS-M2-1 |
| S2 状态与晶圆 | T+3 ~ T+6 周 | 光学变色 / 视野锥 / Wafer Map / 缺陷点云 + **真机联调窗口 A** | MS-M2-2 |
| S3 联动与特效 | T+6 ~ T+9 周 | 点选钻取四链路 + 跟随模式 + FX 框架与首批特效 | MS-M2-3 |
| S4 长跑与验收 | T+9 ~ T+12 周 | **真机联调窗口 B：72h 长跑**、性能调优、验收评审 | MS-M2-4 |

---

## 2. 任务分解

任务编号 TA.<n>；每个任务含【产出 / 依赖 / 验证】；验证方式分 TDD（Core 层）与验证清单（渲染层）。

### S1 数据管线升级（W1–W3）

| 任务 | 产出 | 依赖 | 验证 |
|---|---|---|---|
| TA.0 契约承接确认 | `docs/m2-contract-baseline.md`：M1 契约清单核对 + 本期新增契约（ThrottlePolicy 等）评审冻结 | M1 契约 | 评审签字；CI 基线全绿 |
| TA.1 节流分发 | `src/AoiTwin.Core/Bus/ThrottlePolicy.cs`、`RealtimeHub.Subscribe` 节流重载；策略配置文件热加载 | TA.0 | TDD：200ms 窗口合并保留最新、事件直推、按片整片、策略热加载；延迟断言（轴 ≤ 300ms / 事件 ≤ 50ms，PRD FR-M2-01） |
| TA.2 VisualViewModel 骨架 | `src/AoiTwin.ViewModel/`：`IMotionPartViewModel`（双缓冲位姿副本，无锁交换）、渲染循环挂点 | TA.1 | TDD：数据线程写 / 渲染线程读并发用例；零共享可变状态评审 |
| TA.3 坐标换算与插值 | `src/AoiTwin.Core/Kinematics/TeachCoordinateMapper.cs`、`TransformInterpolator.cs`；标定文件随资产包 | TA.2 | TDD：插值单调时间戳防回跳、Sample 连续性；W1 用 M1 录制数据完成标定（PRD 风险①） |
| TA.4 四部件运动同步 | Robot(R/θ/Z)、Stage(X/Y/Z/θ+升降)、GateAxis_Z、PreAligner 挂 Transform | TA.3 | 回放数据驱动四部件运动无可见抖动；虚拟/实际偏差 ≤ 1 轴数据周期（验证清单 + 录屏） |

**S1 退出**：回放数据驱动四部件运动无抖动（PRD MS-M2-1 退出条件）。

### S2 状态与晶圆（W4–W6）

| 任务 | 产出 | 依赖 | 验证 |
|---|---|---|---|
| TA.5 光学状态映射 | Light_Ring 8 通道变色（映射表配置化）、Light_Coax 亮度自发光 | TA.2 | 验证清单：变色延迟 ≤ 100ms（PRD FR-M2-04）；映射配置热加载 |
| TA.6 视野锥与扫描进度 | Camera_LineScan Frustum 可视化 + 进度线（500ms） | TA.5 | 视野锥准确覆盖扫描区域（对照 Wafer 结果叠加验证） |
| TA.7 Wafer Map 纹理 | `IWaferDataViewModel`：GPU Texture 替换（整片事件，不重建几何体） | TA.2 | 整片刷新掉帧 < 5fps（性能探针，NFR-11） |
| TA.8 缺陷点云增量 | InstancedMesh + BufferAttribute 局部追加（O(增量)） | TA.7 | 追加 1 万缺陷点无全量重建（探针断言）；着色按等级 |
| TA.9 真机联调窗口 A | 现场标定系数入库；三链路（运动/光学/晶圆）真机演示 | TA.4–TA.8 | 现场演示通过（PRD MS-M2-2 退出条件）；每次联调附带 24h 压测 |

### S3 联动与特效（W7–W9）

| 任务 | 产出 | 依赖 | 验证 |
|---|---|---|---|
| TA.10 FX 按需渲染框架 | `src/AoiTwin.Rendering/Fx/IFxOverlay.cs` + `PartFxKind`（本期 3 项）+ **预留 M3 扩展命名规范**（见 §4 协调节点 ①） | TA.2 | 特效开关不影响基线帧率；无特效时不进 Draw Call |
| TA.11 首批特效 | 选中描边（复用 M1 Stencil）、报警脉冲、传输粒子流 | TA.10 | 脉冲与 PLC 报警同步触发/解除 |
| TA.12 部件钻取联动 | `IPartDetailDrillDown`：临时订阅 + 右侧面板切换部件详情（IO/寿命计数/最近报警） | TA.1 | 切换 ≤ 200ms；取消选中订阅释放（无泄漏） |
| TA.13 右键诊断跳转 | 按部件类型跳转 2D 诊断页 | TA.12 | 跳转页面正确（P1，可顺延） |
| TA.14 缺陷双向联动 | 2D 列表→3D 高亮（≤ 100ms）→点击调原图；3D 点→2D 滚动定位 | TA.8 | 双向四条链路演示 |
| TA.15 跟随模式 | 视角锁定载台/机械手，解除恢复 | TA.4 | 追踪平滑无抖动（P1） |
| TA.16 占位/装载状态 | Track 晶圆占位、LoadPort Cassette 状态事件驱动 | TA.10 | 状态变化与 StatusEvent 一致（P1） |

### S4 长跑与验收（W10–W12）

| 任务 | 产出 | 依赖 | 验证 |
|---|---|---|---|
| TA.17 回放回归模式 | 自动化回归套件：M1 录制数据驱动 TA.4–TA.8 全链路，接入 CI | TA.4–TA.8 | 回归套件 CI 全绿（PRD §7-6） |
| TA.18 真机联调窗口 B + 72h 长跑 | `docs/m2-stability-report.md` | 全部 | 无崩溃/内存增长 < 100MB/帧率衰减 < 5%（NFR-14） |
| TA.19 性能报告 | `docs/m2-perf-report.md`：NFR-11/12/13/17 逐条（含核显机降级验证） | TA.18 | 报告达标评审 |
| TA.20 验收与文档 | `docs/m2-acceptance-checklist.md`；PRD/README/版本历史同步；对 M3 契约冻结清单（§4 协调节点 ②） | TA.19 | 评审签字 → 进入 M3 |

---

## 3. 交付物清单

- 代码：`AoiTwin.ViewModel`（新增工程）、`AoiTwin.Core` 节流扩展、`AoiTwin.Rendering` 动态场景/FX/联动；
- 报告：稳定性（72h）、性能、真机标定系数、验收清单；
- 契约：对 M3 冻结清单（VisualViewModel / FX 框架 / 钻取面板 / 颜色映射 schema）；
- CI：回放回归套件入门槛。

## 4. 与 M3 的同步协调关键节点（跨期，双方计划各留一份）

| # | 协调节点 | 时间点 | 内容 |
|---|---|---|---|
| ① | **PartFxKind 枚举预留** | M2 TA.10（约 T+6 周） | M3 将追加 `AlertState / DefectHeatmap / KpiBadge`。本期冻结时：枚举值追加不改名、新增 Kind 不得修改渲染主循环——M3 以纯增量方式接入 |
| ② | **M2→M3 契约冻结清单** | M2 验收（T+12 周） | VisualViewModel 契约、GPU 上传队列、钻取面板框架、颜色映射 schema（`stateColors` 预留 `Warning/Critical` 键位）。冻结后 M3 只读复用 |
| ③ | **时序数据沉淀提前启动** | M2 W10（72h 长跑窗口） | ⚠️ 最重要的跨期依赖：M3 光源衰减/轴磨损回测需要真实时序数据，而 Sink 属 M3（TB.0）。**处置：M3 的 TB.0（持久化 Sink 最小版）提前 2 周并行开发，随 M2 72h 长跑一并部署采集**；并开发 `.aotrec → 时序库`回灌工具（M1 录制文件补数据）。否则 M3-W4 回测将无数据可用（风险 R-1） |
| ④ | **UI3D 浮层骨架前移** | M2 TA.10 一并实现 | M3 的 KPI 标注（FR-M3-11）依赖世界坐标→屏幕投影 + WPF Adorner 复用 + 1s 节流。该骨架成本在 M2 FX 框架内顺带完成，M3 只挂 KPI 内容 |
| ⑤ | **真机窗口衔接** | M2 窗口 B（T+10~12 周） | M3 不再单独申请窗口：窗口 B 期间同步部署 Sink 采集；M3 现场工作以"数据回测 + 远程数据"为主，仅预留 1 个应急窗口 |
| ⑥ | **包升级窗口** | M3 开始前 | HelixToolkit 等依赖升级（为 LOD0 PBR）在 M2 验收后、M3 启动时一次性完成，M2 期间锁版本 |

## 5. 潜在风险与应对

| 风险 | 触发信号 | 应对 |
|---|---|---|
| R-1 时序数据不足导致 M3 回测延期 | M2-W10 未部署 Sink | 协调节点 ③：Sink 提前并行 + 录制文件回灌工具兜底 |
| 轴坐标标定失败 | 窗口 A 虚拟/实际偏差超限 | TA.3 换算模块独立 + W1 即用录制数据预标定；现场仅微调 |
| 长跑内存泄漏/帧率衰减 | 24h 压测增长超阈 | W4 起每窗口附带 24h 压测，问题当周闭环；热路径零 GC 抽查 |
| 联动链路复杂度超预期 | TA.14 双向联动联调延期 | 联动走统一的 PartId/DefectId 寻址服务，避免各链路私连 |
| 跨期人力切换断档 | M2 验收后 A 线转 M3 渲染终调 | M2 验收前完成契约冻结清单评审，M3 任务按依赖就绪度排入 |
