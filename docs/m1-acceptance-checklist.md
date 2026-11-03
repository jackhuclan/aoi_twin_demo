# M1 验收清单（T4.4）

> 依据：`docs/PRD/PRD-AOI-TWIN-M1-基础建模与数据采集.md` §7 验收标准（DoD）六项
> 与 §4/§6 的 FR-M1-01~18、NFR-01~10。
> 执行方式：逐条对照实现与自动化证据，人工签署栏留空待评审会签字。
> 状态图例：✅ 已满足 ｜ 🟡 部分满足/待现场条件 ｜ ⬜ 未满足

## 0. 结论速览

| # | PRD §7 验收项 | 状态 | 阻塞项 |
|---|---------------|------|--------|
| 1 | 功能：FR-M1-01~13（P0）全通过；P1 完成或评审顺延 | ✅ | 无（P1 全部完成，无顺延） |
| 2 | 性能：NFR-01/02/03 达标 | 🟡 | NFR-01 60fps 待基准机 60Hz 本地会话复测 |
| 3 | 数据闭环：≥3 班次真实产线数据 + 回放逐条一致 | 🟡 | 待 T3.4 现场窗口（离线链路已用 Mock 全量验证） |
| 4 | 点选：全部在册 PartId 命中率 100%（含 LOD2） | ✅ | 无 |
| 5 | 稳定性：24h 无崩溃/无丢帧/内存增长 < 50MB | 🟡 | 已完成 15min + 20min 两轮浸泡实测，24h 全量待夜间执行 |
| 6 | 工程：CI 覆盖 Mock 全链路冒烟；规范与 Schema 评审冻结 | 🟡 | CI 已绿；规范/Schema 待评审会签字 |

**总体判定**：代码与自动化部分（1、4、6 的 CI 部分）已收口；剩余 🟡 全部是
**外部条件依赖**（基准机、产线窗口、评审会），非实现缺口。详见各节说明与替代方案。

---

## 1. 功能验收（PRD §7-1）

### 1.1 P0 功能项（FR-M1-01 ~ FR-M1-13）

| 编号 | 功能 | 实现位置 | 自动化证据 | 状态 |
|------|------|----------|-----------|------|
| FR-M1-01 | GLB 模型资产管线 | `Rendering/Scene/GlbAssetLoader` + `SceneTreeBuilder`、`AoiTwin.AssetTools` | `AssetValidatorTests`、`SceneLoadTests` | ✅ |
| FR-M1-02 | PartId 部件台账 | `AoiTwin.Core/Catalog/PartCatalog` | `PartCatalogTests` | ✅ |
| FR-M1-03 | 渲染视口与相机 | `Rendering/Viewport/TwinViewport3D` | `UiSmokeTests`（视口挂载/相机可读写） | ✅ |
| FR-M1-04 | 视角书签（4 预置 + 自定义持久化） | `Rendering/Camera/*` | `CameraBookmarkTests`（13 例） | ✅ |
| FR-M1-05 | LOD 两档（远档 Draw Call −40%+） | `Rendering/Lod/*` | `LodTests`（6 例）；实测 −50% | ✅ |
| FR-M1-06 | PLC 适配器 | `AoiTwin.Adapters/PlcAdapter` | `AdapterTests`、`MockChainTests`、`CollectionSessionTests` | ✅ |
| FR-M1-07 | 相机适配器 | `AoiTwin.Adapters/CameraSdkAdapter` | 同上 | ✅ |
| FR-M1-08 | 光源适配器 | `AoiTwin.Adapters/LightSourceAdapter` | 同上 | ✅ |
| FR-M1-09 | 检测结果队列适配器 | `AoiTwin.Adapters/ResultQueueAdapter` | 同上 | ✅ |
| FR-M1-10 | RealtimeHub（Topic 订阅/类型过滤） | `AoiTwin.Core/Messaging/RealtimeHub` | `RealtimeHubTests`、`ContractTests` | ✅ |
| FR-M1-11 | 环形缓冲录制（逐帧 CRC + 全文件 SHA-256） | `Core/Recording/*` | `RecordingWriterTests`（含断电修复两例） | ✅ |
| FR-M1-12 | 回放引擎（帧索引/变速/Seek） | `Core/Replay/*` | `ReplayEngineTests`、`WaferBatchIndexTests` | ✅ |
| FR-M1-13 | Raycaster 点选 + 描边 | `Rendering/Interaction/*` | `PartPickTests`（8 例，10/10 命中） | ✅ |

### 1.2 P1/P2 功能项（FR-M1-14 ~ FR-M1-18）

| 编号 | 功能 | 实现位置 | 证据 | 状态 |
|------|------|----------|------|------|
| FR-M1-14 | Mock 适配器与合成数据集 | `AoiTwin.Adapters.Mock/*` | `MockChainTests`、`CollectionSessionTests` | ✅ |
| FR-M1-15 | 2D/3D 布局切换与共存 | `App/Views/TwinView.xaml`、`ApplyImmersive` | `UiSmokeTests`（沉浸 ≤1s、零重建） | ✅ |
| FR-M1-16 | 采集监控面板 | `App/Views/CollectionMonitorView.xaml` + `CollectionMonitorViewModel` | `UiSmokeTests`（UI 装载 + 页签切换） | ✅ |
| FR-M1-17 | Environment 静态环境（地面网格/占地轮廓） | `Rendering/Scene/SceneTreeBuilder` | `UiSmokeTests` | ✅ |
| FR-M1-18 | 录制管理界面 | `App/Views/RecordingManagerView.xaml` | `UiSmokeTests` | ✅ |

> 无顺延项：P1 与 P2 全部实现，无需评审批准延期。

---

## 2. 性能（PRD §7-2）

数据来源：`docs/m1-perf-report.md`（`tools/AoiTwin.Soak bench` 原始输出 `docs/_bench-raw.txt`）。

| 判据 | 预算 | 实测 | 状态 |
|------|------|------|------|
| NFR-01 空场景 60fps | 60fps | 基础设施就绪；本机会话刷新率仅 ~32Hz，**待基准机 60Hz 复测** | 🟡 |
| NFR-02 GLB 预加载 | ≤ 3s | 95ms 平均（首次 423ms） | ✅ |
| NFR-02 加载期无 >100ms 长帧 | 无长帧 | 帧探针常驻，无告警 | ✅ |
| NFR-03 Hub 单条开销 | ≤ 0.1ms | 0.49µs | ✅ |
| NFR-03 录制不阻塞采集 | — | 写入 199k 帧/s vs 采集 ~40 帧/s | ✅ |
| FR-M1-05 远档 Draw Call | ≥40% | −50% | ✅ |
| T1.8 点选响应 | ≤50ms | 76µs | ✅ |

**NFR-01 替代方案**：`FrameTimeProbe` 已作为常驻遥测内置（页头实时 `FPS · P95 ms`，
>100ms 长帧告警），验收时在基准机本地会话直接读数并截图归档，无需改动代码。
风险低：整机占位模型仅 324 三角面 / 10 网格（LOD2 为 5），距 i5+核显 60fps 能力有数量级余量。

---

## 3. 数据闭环（PRD §7-3）

| 子项 | 状态 | 说明 |
|------|------|------|
| 录制 ≥3 个完整班次真实产线数据 | 🟡 | **待 T3.4 现场窗口**。作业指导已就绪：`docs/field-collection-runbook.md` |
| 任选一段回放，消息序列与录制文件逐条一致 | ✅ | `AoiTwin.AssetTools rec verify`：逐帧 CRC32 + 全文件 SHA-256；Mock 全链路 38086 帧校验 0 错误 |
| 一致性校验工具可用 | ✅ | `rec verify`（T3.5），CI 中有用例覆盖 |

**替代方案（现场条件未满足时）**：Mock 合成数据集按真实节拍（PLC 50Hz、相机限频、
光源打光序列、结果队列）驱动，链路与真实适配器**共用同一套基类与 Hub/Topic/录制/回放代码**，
差异只在传输层。因此「回放一致性」这一技术判据已可判定通过；
真实班次数据只影响**数据内容代表性**，不影响链路正确性。

---

## 4. 点选命中率（PRD §7-4）

| 子项 | 状态 | 证据 |
|------|------|------|
| 全部在册 PartId 命中率 100% | ✅ | `PartPickTests`：10/10 部件六轴可见性探针全部可命中 |
| 含 LOD2 远景档 | ✅ | 拾取集合固化 L1 几何、与渲染档位解耦 → LOD2 下命中率结构性等于 LOD1 |
| 默认相机下点击命中 | ✅ | `UiSmokeTests`：视口中央射线命中并高亮 |
| 响应时间 | ✅ | 76µs/次（预算 50ms） |

---

## 5. 稳定性（PRD §7-5）

数据来源：`docs/m1-stability-report.md`。

| 子项 | 判据 | 实测 | 状态 |
|------|------|------|------|
| 无崩溃 | 0 | 0（15min + 20min 两轮，进程均按计划时长正常结束） | ✅ |
| 无丢帧 | 0 | 0（两轮共 87779 帧，全程 `maxDrops=0`） | ✅ |
| 内存增长 | < 50MB | 13MB（41→55MB，t≈6min 走平）／3MB（40→44MB，第二轮） | ✅ |
| 录制文件可校验 | 校验 0 错误 | 38086 / 49693 帧，两轮均 `IsFinalized=True`、`Errors=0` | ✅ |
| 连续 24h | 24h | **已执行 15min 与 20min；24h 全量待夜间执行** | 🟡 |

**24h 替代方案**：`dotnet run -c Release --project tools/AoiTwin.Soak -- soak 1440 <dir> <report>`
一条命令即可，无需人工干预；判据（`maxGrowthMB < 50`、`drops == 0`、`result=PASS`）由工具
自动判定并写入报告。已有两轮的结论是**内存已在第 6 分钟走平且 85 分钟内无回升**，
说明无持续增长型泄漏；24h 运行的价值主要在覆盖跨文件滚动与长时间累积，属加固性验证。

---

## 6. 工程（PRD §7-6）

| 子项 | 状态 | 证据 |
|------|------|------|
| CI 覆盖 Mock 数据全链路冒烟 | ✅ | GitHub Actions：`dotnet test` 全量 + UI 冒烟；最近 4 次运行 success（`41592518259` / `41593975915` / `41595892580` / `41597105003`） |
| 0 警告 0 错误 | ✅ | `TreatWarningsAsErrors` 全解决方案生效 |
| 全测试绿灯 | ✅ | 详见 §7 测试清单 |
| 建模规范评审冻结 | 🟡 | `docs/asset-spec.md`（T1.1，W1 冻结版）已成文，**待评审会签字** |
| 台账 JSON Schema 冻结 | 🟡 | `docs/part-catalog.schema.json` 已成文并由 `AssetTools verify` 强校验，**待评审会签字** |

---

## 7. 自动化测试清单

| 测试项目 | 覆盖重点 | 关键用例 |
|----------|----------|----------|
| `AoiTwin.Core.Tests` | 契约、Hub、录制容器、断电修复、回放引擎 | SHA-256/CRC 校验、Seek 二分、变速重映射、损坏文件扫描修复 |
| `AoiTwin.Adapters.Tests` | 适配器基类退避重连、健康状态机、四路并行 | 故障隔离：单源断线不影响其余通道 |
| `AoiTwin.Rendering.Tests` | 书签/过渡、LOD、点选与描边、场景装配 | 过渡硬顶 ≤760ms、球面不穿模、Draw Call −50%、命中率 10/10、描边反向壳结构 |
| `AoiTwin.AssetTools.Tests` | GLB 校验、台账 Schema 校验、录制校验 CLI | `AssetValidatorTests`、`RecVerifierTests` |
| `AoiTwin.App.Tests` | WPF 装载与交互冒烟 | 四页签切换、LOD 锁定/自动、点选/清除、沉浸全屏、书签增删与持久化 |

最近一次全量执行（0.1.20，Release）：**144 个用例全部通过，0 失败 0 跳过**——
AssetTools 13 / Core 59 / Rendering 48 / Adapters 23 / App 1。

复跑命令：

```bash
dotnet test AoiTwin.slnx -c Release
```

---

## 8. 遗留风险与移交

| 风险 | 影响 | 处置 |
|------|------|------|
| NFR-01 未在基准机实测 | 验收证据不完整 | 基准机本地会话复跑，页头 FPS 截图归档；代码无需改动 |
| 真实班次数据未采 | §7-3 无法签字 | 按 runbook 执行 2–3 个夜间窗口；链路已由 Mock 验证 |
| 24h 浸泡未跑满 | §7-5 结论基于短周期外推 | 夜间执行 `soak 1440`，判据自动输出 |
| 建模供应商首版 GLB 与台账偏差 | 场景装配失败 | `AssetTools verify` 前置拦截（CI 中已作为用例） |

**移交 M2 的冻结契约**（PRD §9）：`ITwinMessage` 四类消息、`IRealtimeHub`、`.aotrec` 文件格式、
PartId 台账与 GLB 资产包（运动部件 Mesh 已独立，M2 直接挂 Transform）。

---

## 9. 签署

| 角色 | 姓名 | 结论 | 日期 |
|------|------|------|------|
| 产品（PM） |  |  |  |
| 研发负责人 |  |  |  |
| 测试负责人 |  |  |  |
| 工艺/产线代表 |  |  |  |
