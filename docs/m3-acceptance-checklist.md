# M3 验收清单（TB.20）

> 依据：`docs/PRD/PRD-AOI-TWIN-M3-预测分析与优化.md` §7 验收标准（DoD）七项。
> 状态图例：✅ 离线满足（有自动化证据）｜🟡 部分（需真机/资产）｜⬜ 未满足
> 执行方式：逐条对照实现与证据；人工签署栏留空待评审会。

---

## 0. 结论速览

| # | PRD §7 验收项 | 状态 | 阻塞项 |
|---|---|---|---|
| 1 | 功能：FR-M3-01~09、12、13、15（P0）通过；P1 完成或顺延 | ✅ | 无（P1 亦全部实现） |
| 2 | 预测有效性：光衰回测提前量 ≥ 7 天 + 误报率报告；轴磨损试运行 ≥ 2 周 | 🟡 | 提前量已离线回测通过；**真实误报率与轴试运行需现场** |
| 3 | 数据闭环：30 天时序完整（抽样）+ 任意区间回灌复现 | 🟡 | 离线 30 天合成链路通过；真实 30 天数据待 Sink 常驻 |
| 4 | 性能：NFR-19~21（A/B：合批 Draw Call、LOD0 帧率、上传卡顿消除） | 🟡 | 合批/上传离线达标；**LOD0 帧率待资产与基准机** |
| 5 | 长稳：30 天连续运行报告（NFR-22/23） | 🟡 | 离线加速版通过；真机 30 天待窗口 |
| 6 | 扩展性：插件新增 1 模型 + 1 路数据源，Hub/渲染零改动 | ✅ | 无（有结构断言） |
| 7 | 文档：白皮书、接入指南、插件开发指南评审 | 🟡 | 文档已成文（本批）；待评审签字 |

**总体判定**：实现层全部收口（43 例全绿、0 警告 0 错误）；剩余 🟡 全部为外部条件
（真实数据、LOD0 资产、30 天窗口、评审会），不是代码缺口。

---

## 1. 功能验收（PRD §7-1）

| 编号 | 功能 | 实现位置 | 自动化证据 | 状态 |
|---|---|---|---|---|
| FR-M3-01 | 持久化 Sink（批量/重试/降级） | `Core/Persistence/PersistenceSink` | `PersistenceTests` 4 例 | ✅ |
| FR-M3-02 | 存储治理（保留/水位/清理） | `Core/Persistence/StorageGovernor` | 2 例 | ✅ |
| FR-M3-03 | 历史数据回灌（按日期区间） | `Core/Persistence/TimeSeriesReplayEngine` | 回放顺序/时间戳重映射 1 例 | ✅ |
| FR-M3-04 | 回灌复用同一注入通道 | 同上（`IRealtimeHub.Publish`） | 3D 场景无需改动即可复用 | ✅ |
| FR-M3-05 | 健康度评分（可配置、可解释） | `Core/Analytics/{HealthScorer,AnalyticsOptionsFile}` | 2 例（扣分项、等级）+ 6 例（参数文件：缺省/容错/越界拒绝/保旧值） | ✅ |
| FR-M3-06 | 光源衰减预测（提前量 ≥7 天） | `Predictors.LightDecayPredictor` | 3 例（含 7 天回测） | ✅ |
| FR-M3-07 | 轴磨损预警 | `Predictors.AxisWearPredictor` | 2 例 | ✅（代理量待真机交叉验证） |
| FR-M3-08 | 节拍劣化 + 瓶颈 Top3 | `Predictors.TactTrendAnalyzer` | 1 例 | ✅（站点来源待真机换字段） |
| FR-M3-09 | 预警回流 3D（Alert Topic + FX） | `AlertEvent`、`AlertScheduler`、`Rendering/Fx/AlertStateFx` | 调度 1 例 + 颜色映射 1 例 | ✅ |
| FR-M3-12 | LOD0 特写档 + 三档切换 | `LodLevel.L0`、`LodLevelSelector` | 1 例 | ✅ 代码 / 🟡 资产 |
| FR-M3-13 | 缺陷热力晕染 | `Rendering/Fx/DefectHeatmapFx` | 1 例 | ✅ |
| FR-M3-15 | 对外只读导出（CSV/JSON + 脱敏 + ACL） | `Core/Analytics/Export` | 4 例 | ✅ |
| FR-M3-11 | UI3D KPI 标注（P1） | `Rendering/Overlay/KpiBadgeOverlay` | 投影 1 例 | ✅ |
| FR-M3-14 | 静态合批（P1） | `Rendering/Lod/StaticBatcher` | 1 例 | ✅ |
| FR-M3-10 | 上传分帧（P1） | `Rendering/Upload/UploadBudgetGate` | 2 例 | ✅ |

> P1 无顺延项：`KPI 标注 / 静态合批 / 上传分帧` 均已实现并有测试。

---

## 2. 预测有效性（PRD §7-2）

| 子项 | 判据 | 离线证据 | 状态 |
|---|---|---|---|
| 光衰提前量 | 首次预警时剩余 ≥ 7 天 | `Light_decay_gives_seven_day_lead_time_on_backtest_window`：剩余 7 天时报出，`HorizonDays ∈ [6,7]` | ✅ |
| 光衰置信度 | 预测须带 R² / 区间 | `AlertEvent.Confidence` + 因子「拟合优度」「距预警线 ±天」 | ✅ |
| 误报率报告 | 2 周运行统计 | — | 🟡 需现场 |
| 轴磨损试运行 | ≥ 2 周调参记录 | — | 🟡 需现场（记录表见 `m3-field-verification.md` §3.5） |

**误报率报告模板**（现场填写）：

| 周期 | 预警数 | 真实故障 | 误报 | 误报率 | 处置 |
|---|---|---|---|---|---|
| 第 1 周 | | | | | |
| 第 2 周 | | | | | |

---

## 3. 数据闭环（PRD §7-3）

| 子项 | 证据 | 状态 |
|---|---|---|
| 30 天写入完整（抽样校验） | `SoakSimulationTests`：2 160 条写入、`sinkHealthy=True`、分区 28–31 | ✅ 离线 |
| 任意区间回灌复现 | `History_replay_publishes_records_in_order_with_remapped_timestamps` | ✅ |
| 真实 30 天数据 | `tools/AoiTwin.Soak analytics 30 <root> <report>` 现场复跑 | 🟡 待窗口 |

---

## 4. 性能（PRD §7-4）

| 编号 | 判据 | 离线实测 | 状态 |
|---|---|---|---|
| NFR-19 | LOD0 ≥45fps | — | 🟡 待资产 + 基准机 |
| NFR-20 | LOD2 Draw Call 较 M2 下降 ≥40% | 合批单测：4 部件 → 3 组；真实 16 部件按 (运动组,颜色) 预计 16 → 6~8（−50%~−62%），**需现场 A/B 复核** | 🟡 部分 |
| NFR-21 | 30 天区间查询 ≤3s | 离线 2 160 条全量扫描 560ms（含分析+导出）；真实量级待测 | 🟡 部分 |
| 上传分帧 | 单帧 ≤4MB、无卡顿 | `UploadBudgetGate` 2 例（预算/优先级/超大独占） | ✅ |
| 热力层 | ≤8 Draw Call | 单测：层数 ≤ bands | ✅ |

> 现场 A/B 口径：以可视树中 `GeometryModel3D` 数量等价 Draw Call（与 M1/M2 报告一致）。

---

## 5. 长稳（PRD §7-5）

| 项 | 证据 | 状态 |
|---|---|---|
| 离线加速 30 天 | `SoakSimulationTests` + `analytics 30`（2160 条、560ms、内存增长 < 64MB） | ✅ |
| 真机 30 天 | 待窗口；判据：无崩溃 / 分区 ≤31 / 磁盘线性 / 误报可控 | 🟡 |

---

## 6. 扩展性（PRD §7-6）

| 子项 | 证据 | 状态 |
|---|---|---|
| 新增 1 个预测模型零改动 | `ExtensibilityTests`：注册即生效，`AlertKind.Custom` 回流 | ✅ |
| 新增 1 路数据源零改动 | 复用 `StatusEvent` 自定义 `EventCode`（VIBRATION_RMS） | ✅ |
| Hub / 渲染层无改动 | 结构断言：`AoiTwin.Core` 不引用 `AoiTwin.Rendering` / `PresentationFramework` / `WindowsBase` | ✅ |

---

## 7. 文档（PRD §7-7）

| 文档 | 状态 |
|---|---|
| `docs/m3-implementation-status.md`（实现对照 + 实测 + 偏离说明） | ✅ 待评审 |
| `docs/m3-field-verification.md`（真机验证点） | ✅ 待评审 |
| `docs/m3-plugin-guide.md`（插件开发指南） | ✅ 待评审 |
| `docs/architecture-whitepaper-v0.1.md`（§9 M3 增量） | ✅ 待评审 |
| `README.md` / `PLAN-AOI-TWIN-M3`（功能表、测试矩阵 298、`analytics` 用法、§6 执行进度） | ✅ |

---

## 8. 测试与工程门槛

| 项 | 实测 | 状态 |
|---|---|---|
| 构建 | `dotnet build AoiTwin.slnx -c Release` → 0 警告 0 错误 | ✅ |
| M3 测试 | `AoiTwin.Analytics.Tests` **43 例全绿**（517 ms） | ✅ |
| 全量测试 | **298 例**（M1+M2 255 + M3 43），见 README 测试矩阵 | ✅ |
| 分层 | 解决方案按 Solution Items / src / tests / tools 组织（`AGENTS.md`） | ✅ |

---

## 9. 签署

| 角色 | 姓名 | 结论 | 日期 |
|---|---|---|---|
| 产品（PM） | | | |
| 研发负责人 | | | |
| 测试负责人 | | | |
| 工艺/产线代表 | | | |
