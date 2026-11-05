# 实施计划 · AOI 数字孪生三期（M3）：预测分析与优化

| 项 | 内容 |
|---|---|
| 文档编号 | PLAN-AOI-TWIN-M3 |
| 版本 / 状态 | v1.1 / 离线部分已交付（真机项待窗口）；进度见 §6 |
| 上游文档 | PRD-AOI-TWIN-M3 · PLAN-AOI-TWIN-M2（承接其 §4 跨期协调节点） |
| 工作方法 | 同前两期：分析层 TDD + 渲染层验证清单 + 数据回测 |
| 计时基准 | T = M2 验收签字日（协调节点 ③ 使 Sink 采集自 M2-W10 起已在运行） |

---

## 0. 前置依赖（进入 M3 的门槛）

1. M2 验收通过，对 M3 的契约冻结清单已交付（VisualViewModel / FX 框架 / 钻取面板 / 颜色映射 schema）；
2. 时序数据已有存量：Sink 自 M2-W10 运行（协调节点 ③）+ M1 录制文件回灌工具产出历史分区；
3. 真机以远程数据为主，仅保留 1 个应急窗口（M2 窗口 B 期间已完成部署衔接）。

---

## 1. 阶段划分与时间安排

| 阶段 | 周期 | 主题 | 对应 PRD 里程碑 |
|---|---|---|---|
| S1 数据底座 | T+0 ~ T+3 周 | 持久化 Sink 正式化 + 存储治理 + 历史回灌 + 健康档案管道 | MS-M3-1 |
| S2 分析与预警 | T+3 ~ T+6 周 | 健康评分 + 三个预测模型（回测调参）+ Alert 回流 3D 可视化 | MS-M3-2 |
| S3 渲染终调 | T+6 ~ T+9 周 | LOD0 PBR + 合批 + 上传分帧 + 热力晕染 + UI3D KPI | MS-M3-3 |
| S4 验收交付 | T+9 ~ T+12 周 | 插件扩展性演示 + 导出接口 + 30 天长稳 + 白皮书与验收 | MS-M3-4 |

---

## 2. 任务分解

任务编号 TB.<n>；验证方式：分析/存储层 TDD，渲染层验证清单，模型有效性以回测数据说话。

### S1 数据底座（W1–W3）

| 任务 | 产出 | 依赖 | 验证 |
|---|---|---|---|
| TB.0 持久化 Sink 正式化 | `src/AoiTwin.Core/Persistence/PersistenceSink.cs`（SQLite 按天分区 + 批量缓冲 ≤ 5s/5000 条 + 失败重试队列 ≤ 30min）；⚠️ 最小版已在 M2-W10 预部署（协调节点 ③），本任务转正：补齐 Schema 全表（motion/status/optics/result/alert 五表） | M2 契约 | TDD：写失败降级不阻塞实时链路（NFR-22）、批量刷盘边界、日切滚动、断电恢复 |
| TB.1 录制文件回灌工具 | `AoiTwin.AssetTools/RecImporter.cs`：`.aotrec → 时序库`（补齐 Sink 部署前历史） | TB.0 | TDD：回灌产物与原录制文件抽样逐条一致 |
| TB.2 存储治理 | 保留天数配置 + 自动清理 + 磁盘水位 + `ITimeSeriesReader.EstimateBytes` | TB.0 | TDD：清理策略执行正确、水位保护触发；存储增长估算工具输出 |
| TB.3 历史回灌 3D | `IReplayEngine.LoadTimeRangeAsync` 扩展（与文件回放同一注入通道） | TB.0 | TDD + 清单：任意日期区间回放正确（30 天内查询 ≤ 3s，NFR-21） |
| TB.4 健康档案管道 | `PartHealthProfile` 聚合：寿命计数/报警统计/负载分布，随 Topic 持续更新 | TB.0 | TDD：聚合窗口正确、档案与 PartId 全量对齐 |

**S1 退出**：30 天写入方案验证通过，历史回灌复现正确（PRD MS-M3-1 退出条件）。

### S2 分析与预警（W4–W6）

| 任务 | 产出 | 依赖 | 验证 |
|---|---|---|---|
| TB.5 分析层插件框架 | `IAlertPredictor` 注册机制（DLL 插件 + 签名校验，NFR-25）+ `Alert` Topic + 调度器（5min 周期 + 事件触发） | TB.4 | TDD：插件注册/隔离（一个插件崩溃不影响其他）；签名校验拒绝未授权 DLL |
| TB.6 健康度评分与视图 | 加权扣分制评分（权重配置热加载）+ 钻取面板健康视图（评分 + 趋势 + 构成因子） | TB.4 | 评分规则透明可配置（FR-M3-05）；全部在册部件档案完整 |
| TB.7 光源衰减预测 | `LightDecayPredictor`：亮度-时长曲线拟合 → 剩余天数 → Warning/Critical | TB.5 | **回测验证：提前量 ≥ 7 天**（用 Sink 存量 + 回灌数据，PRD §7-2）；置信区间标注 |
| TB.8 轴磨损预警 | `AxisWearPredictor`：重复定位偏差/电流漂移（滚动基线 + σ 带） | TB.5 | 回测通过 + 试运行 ≥ 2 周调参记录 |
| TB.9 节拍劣化分析（P1） | 单站节拍趋势 + 瓶颈工序 Top3 | TB.5 | 趋势图与瓶颈标注演示 |
| TB.10 预警 3D 可视化 | 预警经 `Alert` Topic 回流 → `PartFxKind.AlertState`（状态色升级 + 脉冲，**纯增量接入 M2 FX 框架**）+ 预警卡片（钻取面板） | TB.5 | 预警触发 3D 状态变化；证据因子展示（误报可解释） |

**S2 退出**：回测提前量 ≥ 7 天；预警 3D 联动演示通过（PRD MS-M3-2 退出条件）。

### S3 渲染终调（W7–W9）

| 任务 | 产出 | 依赖 | 验证 |
|---|---|---|---|
| TB.11 LOD0 特写档 | PBR 材质 + 全细节（限相机/光源/载台等高频钻取部件，风险应对）+ 三档按距离/书签双向平滑切换 | M2 渲染 | LOD0 ≥ 45fps；切换无闪烁（PRD FR-M3-12） |
| TB.12 静态合批 | 外壳/轨道/环境合并 Draw Call | TB.11 | LOD2 档 Draw Call 较 M2 下降 ≥ 40%（A/B 报告，NFR-20） |
| TB.13 GPU 上传分帧 | 上传优先级 + 单帧 ≤ 4MB 预算 | TB.12 | 纹理替换不再引起卡顿（探针） |
| TB.14 缺陷热力晕染（P1） | `PartFxKind.DefectHeatmap`（GPU shader 密度热力层，按需渲染） | TB.10 | 热力分布与缺陷点云一致；可开关 |
| TB.15 UI3D KPI 标注（P1） | `PartFxKind.KpiBadge`：在 M2 UI3D 骨架上挂 KPI 内容（1s 节流） | TB.6 | 1920×1080 可读、不遮挡、一键关闭（FR-M3-11） |

**S3 退出**：NFR-19/20 A/B 报告达标（PRD MS-M3-3 退出条件）。

### S4 验收交付（W10–W12）

| 任务 | 产出 | 依赖 | 验证 |
|---|---|---|---|
| TB.16 对外导出接口 | 健康档案/预警历史/时序片段只读导出（CSV/JSON）+ 脱敏选项 | TB.2、TB.6 | 接口文档完备；ACL/脱敏验证（NFR-24） |
| TB.17 扩展性演示 | 现场演示：插件新增 1 个预测模型 + 1 路数据源，**Hub 与渲染层零改动** | TB.5 | US-M3-11 演示通过（PRD §7-6） |
| TB.18 30 天长稳 | `docs/m3-stability-report.md`：无崩溃、清理生效、内存稳定 | TB.12 | NFR-22/23 达标（启动于 W7，与 S3 并行） |
| TB.19 文档收口 | 架构白皮书（ADR）、台账接入指南、插件开发指南；PRD/版本历史同步 | 全部 | 评审通过 |
| TB.20 验收评审 | `docs/m3-acceptance-checklist.md`（PRD §7 六项逐条 + 证据） | TB.18、TB.19 | 评审签字 → 系统进入产线常态运行 |

---

## 3. 交付物清单

- 代码：`AoiTwin.Core/Persistence`、`AoiTwin.Core/Analytics`（插件框架 + 三模型）、`AoiTwin.Rendering` 三档 LOD/合批/新 FX Kind、导出接口；
- 数据资产：时序库历史分区（M1 回灌 + M2 Sink 沉淀）、回测报告与模型参数；
- 文档：白皮书、接入指南、插件开发指南、稳定性/性能/验收报告；
- 能力证明：零改动扩展演示（US-M3-11）。

## 4. 与 M2 的同步协调关键节点（与 PLAN-M2 §4 镜像）

| # | 节点 | 时间点 | 说明 |
|---|---|---|---|
| ① | 接收 PartFxKind 扩展约定 | M3 启动时 | 仅追加 `AlertState / DefectHeatmap / KpiBadge`，不改渲染主循环（M2 TA.10 冻结的规范） |
| ② | 接收契约冻结清单 | M3 W1（TA.0） | VisualViewModel / GPU 上传队列 / 钻取面板 / 颜色映射 schema 只读复用 |
| ③ | **Sink 提前介入（最关键）** | M2-W10 起 | TB.0 最小版在 M2 72h 长跑窗口随行部署采集；M3 W1 转正。同时 TB.1 回灌工具补齐历史。此节点决定 S2 回测是否可行 |
| ④ | 复用 UI3D 骨架 | M3 TB.15 | 投影/节流/Adorner 骨架已在 M2 完成，本期只挂内容 |
| ⑤ | 真机窗口衔接 | M2 窗口 B | 部署衔接已完成；M3 仅预留 1 个应急窗口 |
| ⑥ | 依赖包升级 | M3 W1 | HelixToolkit 升级（LOD0 PBR 前置）一次性完成并跑全量回归 |

## 5. 潜在风险与应对

| 风险 | 触发信号 | 应对 |
|---|---|---|
| R-1 回测数据不足（M2 Sink 部署延误/数据质量差） | M3-W4 回测样本不足 | 三重兜底：M2-W10 提前部署（节点③）+ TB.1 录制回灌补历史 + 模型阈值先保守并标注置信度，试运行期持续校准 |
| 预测误报引发产线不信任 | 试运行误报率超标 | 评分/预警全部携带证据因子（可解释）；阈值可调 + 出具调参记录后再上 3D 强提示 |
| LOD0 建模工作量超预期 | TB.11 建模交付延期 | 范围限定高频钻取部件（相机/光源/载台），其余沿用 LOD1；预算在资产规范中冻结 |
| 合批后渲染回归 | TB.12 A/B 不达标或出现渲染异常 | 合批按静态分组分批实施，每组独立 A/B，可单独回退 |
| 30 天长稳跨验收期 | TB.18 与 TB.20 排期冲突 | 长稳 W7 启动并行 S3；验收评审以"长稳中期数据 + 承诺收尾"先行、最终数据补交 |
| 分析层故障影响实时链路 | TB.0/TB.5 异常扩散 | Sink 与分析均为 Hub 普通订阅方 + 异常边界隔离（NFR-22 设计红线，TDD 断言固化） |

---

## 6. 执行进度

状态图例：✅ 完成（有自动化证据）｜🏭 待真机/资产｜🟡 部分

| 任务 | 状态 | 产出 / 证据 |
|---|---|---|
| TB.0 持久化 Sink | ✅ | `Core/Persistence/{TimeSeriesRecord,ITimeSeriesStore,FileTimeSeriesStore,PersistenceSink}.cs`；`PersistenceTests` 4 例（含写失败不阻塞、缓冲丢最旧） |
| TB.1 录制回灌 | ✅ | `Core/Persistence/RecImporter.cs`；`.aotrec` 逐帧 CRC 后转记录，往返一致 |
| TB.2 存储治理 | ✅ | `Core/Persistence/StorageGovernor.cs`；正常清理 + 水位应急清理 2 例 |
| TB.3 历史回灌 3D | ✅ | `Core/Persistence/TimeSeriesReplayEngine.cs`；顺序/时间戳重映射，走同一 Hub 注入通道 |
| TB.4 健康档案 | ✅ | `Core/Persistence/PartHealth.cs`；行程/报警/亮灯时长/节拍（Welford），乱序安全 |
| TB.5 插件框架 | ✅ | `Core/Analytics/{AlertContracts,IAnalyticsContext,AlertScheduler,PluginLoader}.cs`、`TwinTopic.Alert`；隔离 + 签名默认全拒 |
| TB.6 健康评分 | ✅ | `Core/Analytics/HealthScorer.cs`（扣分制 + 四档）+ `AnalyticsOptionsFile`（权重/阈值文件源，坏配置保旧值） |
| TB.7 光源衰减 | ✅ | `Predictors.LightDecayPredictor`；**回测提前量 7 天**（`HorizonDays ∈ [6,7]`）、R² 与区间 |
| TB.8 轴磨损 | ✅ 代码 / 🏭 口径 | `Predictors.AxisWearPredictor`；代理量需真机 PLC 双值交叉验证（验证清单 §3） |
| TB.9 节拍劣化 | ✅ 代码 / 🏭 站点来源 | `Predictors.TactTrendAnalyzer` + `Bottlenecks(top3)`；站点目前由 `WaferId` 前缀解析 |
| TB.10 预警回流 3D | ✅ | `Rendering/Fx/AlertStateFx.cs` + `AlertScheduler`（去重/升级即发/Info 恢复） |
| TB.11 LOD0 特写档 | ✅ 代码 / 🏭 资产 | `LodLevel.L0`（只追加）+ `LodLevelSelector(supportsClose)` 三档回滞；资产缺失自动退两档 |
| TB.12 静态合批 | ✅ | `Rendering/Lod/StaticBatcher.cs`；按（运动组, 颜色）合并，UV/法线保留 |
| TB.13 上传分帧 | ✅ | `Rendering/Upload/UploadBudgetGate.cs`；4MB/帧、优先级、同 Key 覆盖、超大独占 |
| TB.14 缺陷热力 | ✅ | `Rendering/Fx/DefectHeatmapFx.cs`；密度 8 档合并（≤8 Draw Call）、可开关 |
| TB.15 UI3D KPI | ✅ | `Rendering/Overlay/KpiBadgeOverlay.cs` + `WorldToScreenProjector`（纯数学可测、1s 节流） |
| TB.16 对外导出 | ✅ | `Core/Analytics/Export/AnalyticsExportService.cs`；CSV/JSON 同源、三角色 ACL、脱敏、原子写（4 例） |
| TB.16b 健康页接入 | ✅ | App 第 5 页签：`AnalyticsRuntime`（组合根）+ `HealthAnalyticsViewModel/View`；降级提示条有测试锁定（5 例） |
| TB.17 扩展性演示 | ✅ | `ExtensibilityTests`：新增模型零改动 + 新增数据源（`StatusEvent` 自定义 `EventCode`）+ 结构断言（Core 不引用 Rendering/WPF） |
| TB.18 30 天长稳 | ✅ 离线 / 🏭 真机 | `tools/AoiTwin.Soak analytics`（2160 条 / 31 分区 / 504ms，result=PASS）+ `SoakSimulationTests`（内存 < 64MB） |
| TB.19 文档收口 | ✅ 待评审 | `docs/m3-implementation-status.md`、`m3-field-verification.md`、`m3-plugin-guide.md`、白皮书 §9、README |
| TB.20 验收评审 | ✅ 待签字 | `docs/m3-acceptance-checklist.md`（DoD 七项，实现层全绿） |

**阶段退出条件对照**

| 阶段 | 退出条件 | 现状 |
|---|---|---|
| S1 | 30 天写入方案验证 + 历史回灌复现 | ✅ 离线通过（`analytics 30` + 回灌往返用例） |
| S2 | 回测提前量 ≥7 天 + 预警 3D 联动演示 | ✅ 提前量达标；3D 联动有颜色映射与调度用例，现场演示待窗口 |
| S3 | NFR-19/20 A/B 达标 | 🟡 合批/上传离线达标；LOD0 帧率待资产与基准机 |
| S4 | 长稳 + 文档 + 验收签字 | 🟡 离线长稳与文档完成；真机 30 天与签字待窗口 |

**三处与计划的偏离**（已决定，评审需知悉；详见 `docs/m3-implementation-status.md` §3）：
存储由 SQLite 改 JSONL 按天分区（`ITimeSeriesStore` 可换回）、轴磨损用保持段离散度作代理量、
LOD0 只做档位与选择器（PBR 资产属外部交付）。

**版本**：0.3.0（`Directory.Build.props`）。
