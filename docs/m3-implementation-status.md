# M3 实施状态（TB.0–TB.20）

> 状态：**离线全部实现并通过测试（43 例）**；真机相关项标注 🏭。
> 版本：0.3.0 ｜ 依据：`docs/PLAN-AOI-TWIN-M3-实施计划.md`、`docs/PRD/PRD-AOI-TWIN-M3-预测分析与优化.md`
> 真机验证点见 `docs/m3-field-verification.md`；验收逐条见 `docs/m3-acceptance-checklist.md`。

---

## 0. 一句话结论

三期代码已全部落地：**存储 → 治理 → 回灌 → 档案 → 插件框架 → 三模型 → 预警回流 3D → 渲染终调 → 导出**，
180 分钟真机窗口尚未使用。剩下的不是实现缺口，而是**数据真实性、资产（LOD0）、签名公钥与现场性能**四类外部条件。

---

## 1. 任务对照

| 任务 | 交付物（代码位置） | 状态 | 证据 |
|---|---|---|---|
| TB.0 持久化 Sink | `Core/Persistence/{TimeSeriesRecord,ITimeSeriesStore,FileTimeSeriesStore,PersistenceSink}.cs`：五表（motion/status/optics/result/alert）、按 UTC 天分区、批量 5000/5s、失败重试 ≤30min、缓冲超限丢最旧、异常不扩散 | ✅ | `PersistenceTests.Sink_*`（4 例） |
| TB.1 录制回灌 | `Core/Persistence/RecImporter.cs`：`.aotrec` 逐帧 CRC 校验后转记录，复用录制容器的解析路径 | ✅ | `Rec_importer_round_trips_recording_into_store` |
| TB.2 存储治理 | `Core/Persistence/StorageGovernor.cs`：保留天数、应急清理、水位、增长估算 | ✅ | `Governor_*`（2 例） |
| TB.3 历史回灌 3D | `Core/Persistence/TimeSeriesReplayEngine.cs`：区间查询 → 同一 Hub 注入通道，变速 0.5–8×、Seek 二分、时间戳重映射 | ✅ | `History_replay_publishes_records_in_order_with_remapped_timestamps` |
| TB.4 健康档案 | `Core/Persistence/PartHealth.cs`：行程/报警/亮灯时长/节拍（Welford）、只用消息时间戳、乱序安全 | ✅ | `Part_health_aggregates_*` |
| TB.5 插件框架 | `Core/Analytics/{IAnalyticsContext,PredictorRegistry,AlertScheduler,PluginLoader}.cs`、`TwinTopic.Alert`、`AlertEvent`（证据因子 + 置信度 + 去重键） | ✅ | `Scheduler_*`、`Predictor_registry_isolates_failing_plugin`、`Plugin_loader_rejects_unsigned_assemblies_by_default` |
| TB.6 健康评分 | `Core/Analytics/HealthScorer.cs`：加权扣分、因子可解释、四档；`AnalyticsOptionsFile` + `analytics-options.json`：阈值与权重文件化（坏配置保旧值） | ✅ | `Health_scorer_*`（2 例）+ `OptionsFileTests`（6 例） |
| TB.7 光源衰减 | `Core/Analytics/Predictors.cs` `LightDecayPredictor`：线性回归 + 剩余天数 + R² + 95% 区间 | ✅ | 3 例（含**提前量 7 天回测**） |
| TB.8 轴磨损 | 同文件 `AxisWearPredictor`：保持段离散度代理、z 分数、漂移 µm/天 | ✅ | `Axis_wear_*`（2 例） |
| TB.9 节拍劣化 | 同文件 `TactTrendAnalyzer` + `Bottlenecks(top3)` | ✅ | `Tact_trend_flags_degradation_and_reports_bottleneck` |
| TB.10 预警 3D | `Rendering/Fx/AlertStateFx.cs`（`FxKind.AlertState`，Warning 黄静态 / Critical 红呼吸，Info 摘除） | ✅ | `Alert_state_fx_maps_severity_to_state_colours` |
| TB.11 LOD0 特写档 | `LodLevel.L0`（只追加）、`LodLevelSelector(supportsClose)` 三档 + 回滞、`LodPackage.Close` / `HasClose` / `CreateSelector` | ✅ 代码 / 🏭 资产 | `LOD_selector_offers_L0_only_when_asset_is_available` |
| TB.12 静态合批 | `Rendering/Lod/StaticBatcher.cs`：(运动组, 颜色) 合并，UV/法线保留，A/B 统计 | ✅ | `Static_batcher_reduces_draw_calls_and_preserves_triangles` |
| TB.13 上传分帧 | `Rendering/Upload/UploadBudgetGate.cs`：4MB/帧、优先级、同 Key 覆盖、超大独占 | ✅ | 2 例 |
| TB.14 热力晕染 | `Rendering/Fx/DefectHeatmapFx.cs`：密度分 8 档合并（≤8 Draw Call）、按需开关 | ✅ | `Defect_heatmap_aggregates_density_into_bounded_layers` |
| TB.15 UI3D KPI | `Rendering/Overlay/KpiBadgeOverlay.cs` + `WorldToScreenProjector`（纯数学可测、1s 节流、可关） | ✅ | `World_to_screen_projects_*` |
| TB.16 导出接口 | `Core/Analytics/Export/AnalyticsExportService.cs`：CSV/JSON 同源、脱敏、ACL 三角色、原子写 | ✅ | `ExportTests`（4 例） |
| TB.17 扩展性演示 | `tests/…/ExtensibilityTests.cs`：新增模型零改动 + 结构断言（Core 不引用 Rendering/WPF） | ✅ | 1 例 |
| TB.18 30 天长稳 | `tools/AoiTwin.Soak analytics <days>` + `SoakSimulationTests`（离线加速 30 天） | ✅ 离线 / 🏭 真机 | 见 §2 |
| TB.19 文档 | 本文件、真机验证清单、插件开发指南 | ✅ | — |
| TB.20 验收 | `docs/m3-acceptance-checklist.md` | ✅ 待签字 | — |

---

## 2. 离线实测（本机，`AoiTwin.Soak analytics 30`）

| 项 | 实测 | 说明 |
|---|---|---|
| 合成数据量 | 2 160 条（30 天 × 24 h × 3 通道） | 光源/轴/晶圆各一条/小时 |
| 全链路耗时 | **504 ms**（写入 + 每日治理 + 分析 + 导出） | 单机、Release；带 `--options` 亦为同量级 |
| 分区数 | 31（保留 30 天） | 边界日各占一个分区 |
| 存储体积 | 468 984 B / 2 160 条 ≈ **217 B/条** | JSONL 明文；未压缩 |
| Sink | `sinkHealthy=True`、0 丢弃、0 失败 | 批量与重试路径未被触发 |
| 光衰预测 | 剩余 **3.9 天** → Warning，R²=1.00 | 合成线性衰减 |
| 节拍劣化 | +7.4% → Warning | 6.06s → 6.51s |
| 提前量回测 | 剩余 7 天时首次报出（`HorizonDays ∈ [6,7]`） | 单测 `Light_decay_gives_seven_day_lead_time_on_backtest_window` |
| 健康评分 | STAGE 100 / WAFER_CURRENT 100 | 无报警数据，符合预期 |
| 导出 | health.csv 2 行 / alerts.json 2 条 | 含脱敏与因子 |
| 参数文件 | `--options assets/analytics-options.json` 生效，报告写出 `options.light/axis/tact` | 证据自带口径：同一份数据换阈值结论会变 |

> 这些数字是**结构性证据**，不是真机性能结论。真机数据率、IO 与查询延迟按 `m3-field-verification.md` §1 复测。

---

## 3. 与计划的三处偏离（已决定，需评审知悉）

1. **存储实现由「SQLite 按天分区」改为「JSONL 按天分区」**
   原因：零原生依赖（CI/离线机免安装）、现场可直接查看、删过期数据 = 删文件。
   影响：区间查询为顺序扫描；30 天量级由分区裁剪承担。**接口 `ITimeSeriesStore` 已抽象**，
   若真机查询延迟超 NFR-21，替换实现即可，上层零改动。
2. **轴磨损用「保持段位置离散度」作重复定位偏差的代理量**
   原因：M2 总线只承载位置/速度，没有指令/反馈双值。物理上成立，但必须真机交叉验证（见验证清单 §3）。
3. **LOD0 只做档位与选择器，未包含 PBR 材质与建模**
   原因：L0 资产属外部建模交付。代码在资产缺失时自动退化为两档（`supportsClose=false`），
   不会出现"切到 L0 却什么都没有"。

### 3.1 对 M1/M2 测试的一处调整（透明记录）

追加 `TwinTopic.Alert` 后，M2 的契约用例 `TwinTopic_contains_four_base_topics`（断言语义枚举只有 4 个值）变红。
处理方式不是把 4 改成 5（那会让契约测试失去意义），而是把它改成
`TwinTopic_keeps_base_values_and_only_appends`：断言**前四个基值序号仍为 0–3、后续值连续无空洞**。
理由：Topic 是 `.aotrec` 帧头里的一个字节，M1 录制的文件必须永远可读，真正要守的是「基值不变 + 只追加」，
而不是「总共有几个」。

---

## 4. 已知缺口（按阻塞程度）

| # | 缺口 | 影响 | 处置 |
|---|---|---|---|
| 1 | 无真机时序数据 | 三模型的阈值未经过真实噪声校准；误报率未知 | 窗口 B 起 Sink 常驻，2 周后按 §7-2 出具误报率报告并调参 |
| 2 | L0 资产未到 | NFR-19（LOD0 ≥45fps）无法测 | 资产到货后填 `LodPackage.Close` 并复测 |
| 3 | 插件公钥白名单为空 | 现场无法加载自定义 DLL（安全默认） | 配置公司公钥 token 后验证（验证清单 §6） |
| 4 | 节拍站点靠 WaferId 前缀解析 | 若设备命名不含站点，全部归入 `ALL`，瓶颈分析失效 | 真机改为结果队列的站点字段（验证清单 §4） |

> 原缺口 #5「App 未挂健康页签」已关闭：健康分析页随 0.3.0 接入主界面（见 §5）。

---

## 5. App 集成（已接线）

分析链路随 0.3.0 进入主界面第 5 页签「健康分析」，装配在 `AppServices.AddAoiTwinAnalytics`：

```
IRealtimeHub ─┬─ PersistenceSink ── ITimeSeriesStore（默认 %LOCALAPPDATA%\AoiTwin\timeseries，
              │                      测试/离线用 AnalyticsRuntimeOptions 覆盖到临时目录）
              ├─ PartHealthAggregator（实时档案）
              └─ AlertScheduler ── PredictorRegistry（三模型）+ AnalyticsOptionsProvider
                                     （assets/analytics-options.json，坏文件保旧值）

AnalyticsRuntime（App/Services）= 上述组件的组合根：
  Snapshot()  只读内存（档案 + 评分 + 统计），1Hz 刷新不碰磁盘
  AnalyzeNow() 唯一读库入口（后台线程），发布预警 → Alert Topic → 3D 状态色
  Export*()    Engineer 角色导出健康 CSV / 预警 JSON
HealthAnalyticsViewModel 只做格式化与命令编排；HealthAnalyticsView 全部经命令绑定。
```

降级可见性（页面提示条，均有测试锁定）：时序写失败 →「落盘降级」红条（实时链路不受影响）；
参数文件非法 →「沿用上一次可用配置」黄条；预测器异常 → 列出失败模型与其余结论仍有效。

渲染侧：`AlertStateFx` 与 `AlarmPulseFx` 同参构造（viewpoint/hub/assemblyProvider/overlay），
在 `TwinView.xaml.cs` 装配 FX 的位置一并创建并 `Refresh()` 于换档之后。
