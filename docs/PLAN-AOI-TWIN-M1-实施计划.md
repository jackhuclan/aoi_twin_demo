# 实施计划 · AOI 数字孪生一期（M1）：基础建模与数据采集

| 项 | 内容 |
|---|---|
| 文档编号 | PLAN-AOI-TWIN-M1 |
| 版本 / 状态 | v1.1 / 实施完成（T1.1–T4.5 全部落地，外部条件项见 §8） |
| 上游文档 | PRD-AOI-TWIN-M1（本文所有 FR/NFR/US 编号均指向该 PRD） |
| 工作方法 | 红绿 TDD（Core 层强制）+ 子代理并行开发 + 任务级评审 |
| 交付工程 | `E:\aoi_twin_demo`（解决方案 `AoiTwin.slnx`） |

---

## 0. 任务总览

- 任务编号规则：`T<里程碑号>.<序号>`；每个任务包含【产出文件 / 依赖 / 验证方式】三要素；
- 粒度约定：单个任务 0.5–2 人日；跨任务的公共契约（T1.0）最先冻结，之后可并行；
- TDD 策略：**Core / Adapters / 录制回放层强制红绿循环**（先写失败测试再实现）；
  渲染/UI 层无法单测的部分改用"验证清单 + 截图/性能探针"人工验收（PRD §9 验证习惯）；
- 每个任务完成 = 代码 + 测试 + 提交（中文 Conventional Commits）+ 文档同步（如有）。

---

## 1. 解决方案骨架（T0，全部任务的前置）

**产出：**

```
E:\aoi_twin_demo\
├─ AoiTwin.slnx
├─ Directory.Build.props          # 版本单点、Deterministic、MIT（沿用 Floaticon 仓库规范）
├─ Directory.Packages.props       # CPM：HelixToolkit.Wpf / CommunityToolkit.Mvvm / Microsoft.Extensions.Hosting
├─ .editorconfig                  # 复用 wpf-float-agent 同款（含静态字段 _camelCase 特例）
├─ .gitignore                     # bin/obj/.vs/.workbuddy/release/artifacts
└─ src/
   ├─ AoiTwin.Core/               # net10.0：契约、总线、录制、回放、运动学（无 UI 依赖）
   ├─ AoiTwin.Adapters/           # net10.0：四路适配器基类与实现
   ├─ AoiTwin.Adapters.Mock/      # net10.0：Mock 适配器 + 合成数据集
   ├─ AoiTwin.Rendering/          # net10.0-windows：HelixToolkit 视口、场景树、LOD、FX
   ├─ AoiTwin.App/                # net10.0-windows WinExe：启动壳 + 主界面
   └─ AoiTwin.AssetTools/         # net10.0 console：资产校验工具
tests/
   ├─ AoiTwin.Core.Tests/
   ├─ AoiTwin.Adapters.Tests/
   └─ AoiTwin.AssetTools.Tests/
assets/                           # GLB + 台账 JSON（随资产包版本化）
docs/
```

**验证**：`dotnet build AoiTwin.slnx` 0 警告 0 错误；空解决方案模板含一个冒烟测试且通过。

---

## 2. MS-M1-1 资产与视口（W1–W3）

### T1.0 契约冻结：统一消息模型与总线接口（P0，1 人日）
- 产出：`src/AoiTwin.Core/Bus/TwinTopic.cs`、`ITwinMessage.cs`、`Messages.cs`（AxisSnapshot / StatusEvent / OpticsEvent / WaferResult / WaferMapSnapshot / DefectRecord，签名与 PRD §5.1–5.2 逐字一致）、`IRealtimeHub.cs`
- 依赖：T0
- 验证：record 契约快照测试（序列化往返 + PartId 非空校验）；本任务评审后**冻结**，后续 PRD §9 承诺生效

### T1.1 建模规范文档（P0，1 人日）
- 产出：`docs/asset-spec.md`——GLB 面数预算（整机 ≤ 15 万面）、PartId 命名（大写下划线，与台账一致）、坐标原点/单位约定、LOD 两档命名（`*_l1.glb` / `*_l2.glb`）
- 依赖：无
- 验证：文档评审签字；台账 JSON Schema（`docs/part-catalog.schema.json`）发布

### T1.2 资产校验工具（P0，1.5 人日）
- 产出：`src/AoiTwin.AssetTools/`（console）+ `tests/AoiTwin.AssetTools.Tests/`——校验 PartId 完整性、面数预算、原点偏差、台账与 GLB 节点一一对应
- 依赖：T1.1
- 验证（TDD）：先写 6 个失败用例（缺 PartId / 超面数 / 原点漂移 / 台账孤儿节点…）→ 实现 → 绿

### T1.3 HelixToolkit 视口控件（P0，2 人日）
- 产出：`src/AoiTwin.Rendering/Viewport/TwinViewport3D.cs`（封装 HelixToolkit 视口 + 渲染循环挂点）、`src/AoiTwin.App/` 中 3D 面板页停靠
- 依赖：T0
- 验证：空场景渲染探针（帧时间采样控件）稳定 60fps（NFR-01）；加载期间 UI 线程无 > 100ms 阻塞（NFR-02）

### T1.4 GLB 后台预加载与场景树静态挂载（P0，3 人日）
- 产出：`src/AoiTwin.Rendering/Scene/GlbAssetLoader.cs`（后台线程加载 + 进度回调）、`SceneTreeBuilder.cs`（按台账挂 Environment / AOI_Equipment 静态部分）
- 依赖：T1.3、T1.1 首版 GLB
- 验证：加载 ≤ 3s；场景节点数与台账一致；每个 Mesh 携带 PartId（Tag）；线程模型符合 PRD §5.7（渲染数据不进 UI 线程）

### T1.5 相机控制器与视角书签（P0，2 人日）
- 产出：`src/AoiTwin.Rendering/Camera/CameraBookmarkService.cs`（四个预置书签 + 自定义书签持久化）
- 依赖：T1.3
- 验证：书签切换平滑过渡 ≤ 800ms（动画探针断言时长）；书签保存/加载往返测试

### T1.6 LOD 两档骨架（P0，2.5 人日）
- 产出：`src/AoiTwin.Rendering/Lod/LodSelector.cs`（按相机距离切换 l1/l2 资产；远景隐藏线缆/门缝，运动部件保留 Transform 节点）
- 依赖：T1.4
- 验证：切换无闪烁无材质跳变（录屏对比）；远景档 Draw Call 较 LOD1 下降 ≥ 40%（性能探针，NFR 对应 PRD FR-M1-05）

### T1.7 PartId 台账服务（P0，1 人日）
- 产出：`src/AoiTwin.Core/Catalog/PartCatalog.cs`（加载台账 JSON → PartId 查询）+ 测试
- 依赖：T1.1
- 验证（TDD）：加载/查询/缺 PartId 异常用例

### T1.8 Raycaster 点选 + Stencil 描边（P0，2.5 人日）
- 产出：`src/AoiTwin.Rendering/Interaction/PartPicker.cs`、`SelectionOutlineFx.cs`；右侧信息区静态台账展示
- 依赖：T1.4、T1.7
- 验证：点选响应 ≤ 50ms；**全部在册部件命中率 100%（含 LOD2）**——用台账遍历脚本对每个 PartId 中心点做程序化命中断言（PRD §7-4）

**MS-M1-1 退出检查**：空场景 60fps、点选框架可用、资产校验工具就绪。

---

## 3. MS-M1-2 采集与总线（W4–W6）

### T2.1 RealtimeHub（P0，2 人日）
- 产出：`src/AoiTwin.Core/Bus/RealtimeHub.cs`（类型安全 Pub/Sub，线程安全性证明文档）+ `tests/AoiTwin.Core.Tests/RealtimeHubTests.cs`
- 依赖：T1.0
- 验证（TDD）：多订阅者广播、跨线程发布不进 UI 线程、发布异常隔离、单条开销 ≤ 0.1ms（Benchmark）

### T2.2 录制器：分帧容器 + 环形缓冲（P0，3 人日）
- 产出：`src/AoiTwin.Core/Recording/RecordingWriter.cs`（.aotrec 容器：文件头/帧头/长度/CRC32/SHA-256，班次滚动分文件）、`RingBuffer.cs`（内存缓冲 ≤ 60s，磁盘水位 ≥ 85% 暂停告警）
- 依赖：T2.1
- 验证（TDD，红绿强制）：逐帧 CRC 校验、断电模拟（进程硬杀）后文件可校验修复、丢帧率 0（压测 100 万条消息）、班次边界滚动正确

### T2.3 适配器基类与四路适配器（P0，4 人日）
- 产出：`src/AoiTwin.Adapters/AdapterBase.cs`（断线指数退避重连 1s→30s、健康状态机、异常隔离）+ `PlcAdapter.cs` / `CameraSdkAdapter.cs` / `LightSourceAdapter.cs` / `ResultQueueAdapter.cs`（协议解析与单位换算，原始报文 → 统一数据模型）
- 依赖：T1.0、T2.1
- 验证（TDD）：重连状态机全迁移用例、报文→消息换算用例；协议文档缺失部分先以接口 + TODO 占位（PRD 风险②）

### T2.4 Mock 适配器与合成数据集（P1，2 人日）
- 产出：`src/AoiTwin.Adapters.Mock/`（四路 Mock）+ `assets/mock-dataset/`（≥ 1 个班次合成数据：轴轨迹/光源事件/Wafer 结果流）
- 依赖：T2.3
- 验证（TDD）：Mock 数据驱动 Hub → 录制全链路，产物通过 T2.2 校验（PRD US-M1-08）

### T2.5 采集监控面板（P1，1.5 人日）
- 产出：`src/AoiTwin.App/Views/CollectionMonitorView.xaml`（数据源状态/吞吐率/录制状态/磁盘水位）
- 依赖：T2.3、T2.2
- 验证：拔线/杀进程模拟单源故障，其余通道不受影响（NFR-05）现场演示

**MS-M1-2 退出检查**：Mock 全链路打通，录制文件校验通过。

---

## 4. MS-M1-3 回放闭环（W7–W8）

### T3.1 回放引擎（P0，3 人日）
- 产出：`src/AoiTwin.Core/Replay/ReplayEngine.cs`（加载索引、按原始时间戳重放、变速 0.5×–8×、Seek 二分定位、以回放 Publisher 身份注入 Hub）
- 依赖：T2.2
- 验证（TDD）：**回放消息序列与录制文件逐条一致**（SHA-256 + 逐帧 CRC 比对，PRD §7-3）；变速下消息顺序不变；Seek 后时间戳统一偏移为回放时钟

### T3.2 回放 UI（P0，2 人日）
- 产出：`src/AoiTwin.App/Views/ReplayView.xaml`（时间轴拖拽/变速/暂停/按 Wafer 批次定位）+ `src/AoiTwin.Core/Replay/WaferBatchIndex.cs`
- 依赖：T3.1
- 验证：批次定位准确（用例：随机选 10 个批次，跳转后首条消息即为该批次首帧）；回放不卡 UI

### T3.3 录制管理界面（P2，1 人日）
- 产出：`src/AoiTwin.App/Views/RecordingManagerView.xaml`（文件列表/删除/导出）
- 依赖：T2.2
- 验证：按班次/日期浏览正确；删除有二次确认

### T3.4 产线首次数据采集（P0，现场窗口，0.5 人日开发 + 2–3 个夜间窗口）
- 产出：`assets/recordings/` 下 ≥ 3 个完整班次 `.aotrec`（清单登记：设备号/班次/时长/SHA-256）
- 依赖：T2.2、T2.3 现场配置
- 验证：采集监控面板全程 0 丢帧；文件校验通过

### T3.5 一致性校验工具（P1，1 人日）
- 产出：`src/AoiTwin.AssetTools/RecVerifier.cs`（CLI：校验 .aotrec 完整性并输出报告）
- 依赖：T2.2
- 验证（TDD）：对损坏文件（翻转字节/截断）必须报错

**MS-M1-3 退出检查**：回放一致性校验通过，真实班次数据入库。

---

## 5. MS-M1-4 集成验收（W9–W10）

### T4.1 布局切换与 2D 共存（P1，1.5 人日）
- 产出：`src/AoiTwin.App/MainWindow.xaml`（3D 面板页与配方树/图像映射条/直方图光谱区共存，可停靠/全屏，FR-M1-15）
- 验证：切换 ≤ 1s；两种布局下 3D 帧率无回退

### T4.2 24h 稳定性测试（P0，测试为主，2 人日）
- 产出：`docs/m1-stability-report.md`——Mock 全链路连续 24h：无崩溃/丢帧，内存增长 < 50MB（PRD §7-5）
- 依赖：T2.4、T3.1

### T4.3 性能测试报告（P0，2 人日）
- 产出：`docs/m1-perf-report.md`——NFR-01/02/03 逐条数据（60fps 帧时间分布、GLB 加载时长、Hub 单条开销 Benchmark）
- 依赖：T1.6、T2.1

### T4.4 验收测试用例执行与评审（P0，2 人日）
- 产出：`docs/m1-acceptance-checklist.md`（PRD §7 六项逐条打勾 + 证据链接）；评审签字 → 进入 M2
- 依赖：T4.1–T4.3

### T4.5 文档收口（P1，1 人日）
- 产出：README（构建/运行/采集指引）、架构白皮书 v0.1（Hub/录制/回放类库边界，为三期"复用至 PL/EL"预留）；PRD 更新日期与版本历史行

---

## 6. 并行化与人力分配建议

| 工作流 | 承担任务 | 说明 |
|---|---|---|
| A：图形/渲染 | T1.3–T1.6、T1.8、T4.1 | 与 B 仅在 T1.4 处汇合（先给占位 GLB 即可解耦） |
| B：数据/总线 | T1.0、T1.7、T2.1–T2.3、T3.1、T3.5 | Core 层 TDD 主线 |
| C：现场/工具 | T1.1、T1.2、T2.4、T3.2–T3.4、T4.2–T4.5 | 建模对接 + 现场采集窗口协调 |

关键解耦点：**T1.0 契约冻结**后 A/B/C 三线可全并行；建模（外部供应商）与 T1.2 校验工具并行，W1 末交首版 GLB。

## 7. 提交与 CI 约定

- 提交信息：中文 Conventional Commits（`feat(core): …` / `fix(recording): …` / `docs: …`），每个任务单独成提交；
- CI：`dotnet build` 0 警告 0 错误 + `dotnet test` 全绿为合并门槛；Core/Adapters 测试覆盖率 ≥ 80%（UI/渲染层豁免）；
- 文档（本计划/PRD/报告）与对应代码同批提交；`assets/recordings/` 大文件走本地存储 + 清单登记，不入 Git。

## 8. 风险与应对（对应 PRD §8 风险表）

| 风险 | 触发信号 | 应对 |
|---|---|---|
| 模型还原度不足 | 首版 GLB 校验不过 | T1.2 校验工具 W1 就绪，建模方自检后再交付 |
| PLC/SDK 协议文档缺失 | T2.3 被阻塞 | Mock 先行（T2.4），真实协议后补，采集窗口前必须闭环 |
| 采集窗口难约 | W7 窗口未落实 | W4 起由 C 线专人协调；全链路离线可开发，窗口仅影响 T3.4 |
| HelixToolkit 性能不达标 | T1.6 Draw Call 下降 < 40% | 提前触发合批预研（三期 FR-M3-13 的最小版本前移） |

---

## 9. 实施结果对照（v1.1 收口）

任务状态：✅ 已交付 ｜ 🟡 代码/文档已交付，验收依赖外部条件

| 任务 | 交付物 | 状态 | 版本 / 提交 |
|---|---|---|---|
| T1.0 契约冻结 | `AoiTwin.Core/Messaging/*`（TwinTopic + 四类消息 + IRealtimeHub） | ✅ | 0.1.4 |
| T1.1 建模规范 | `docs/asset-spec.md` | 🟡 待评审签字 | 0.1.2 |
| T1.2 资产校验工具 | `src/AoiTwin.AssetTools`（verify / gen-placeholder / rec verify） | ✅ | 0.1.3 |
| T1.3 视口控件 | `Rendering/Viewport/TwinViewport3D`（含帧时间探针、Environment 地面） | ✅ | 0.1.14 |
| T1.4 GLB 预加载与装配 | `Rendering/Scene/*`（后台加载 + Freeze + 场景树） | ✅ | 0.1.15 |
| T1.5 相机书签 | `Rendering/Camera/*`（4 预置 + 自定义持久化 + 过渡硬顶 760ms） | ✅ | 0.1.16 |
| T1.6 LOD 两档 | `Rendering/Lod/*`（运动组合批 + 距离回滞，Draw Call −50%） | ✅ | 0.1.17 |
| T1.7 PartId 台账 | `Core/Catalog/*` + `docs/part-catalog.schema.json` | 🟡 Schema 待评审签字 | 0.1.4 |
| T1.8 点选 + 描边 | `Rendering/Interaction/*`（Möller–Trumbore + 反向壳描边） | ✅ | 0.1.18 |
| T2.1 RealtimeHub | `Core/Messaging/RealtimeHub` | ✅ | 0.1.4 |
| T2.2 录制容器 | `Core/Recording/*`（CRC32 + SHA-256 + 断电修复 + 滚动） | ✅ | 0.1.5 |
| T2.3 四路适配器 | `Adapters/*`（退避重连 + 健康状态机 + 异常隔离） | ✅ | 0.1.6 |
| T2.4 Mock 与合成数据集 | `AoiTwin.Adapters.Mock` | ✅ | 0.1.7 |
| T2.5 采集监控面板 | `App/ViewModels/CollectionMonitorViewModel` | ✅ | 0.1.13 |
| T3.1 回放引擎 | `Core/Replay/*` | ✅ | 0.1.8 |
| T3.2 回放 UI | `App/Views/ReplayView` | ✅ | 0.1.10 |
| T3.3 录制管理界面 | `App/Views/RecordingManagerView` | ✅ | 0.1.10 |
| T3.4 现场采集 | `docs/field-collection-runbook.md` | 🟡 待现场窗口 | 0.1.20 |
| T3.5 一致性校验 | `AssetTools rec verify` | ✅ | 0.1.8 |
| T4.1 布局切换 | `App/Views/TwinView`（右侧四页签 + 沉浸全屏） | ✅ | 0.1.19 |
| T4.2 稳定性报告 | `docs/m1-stability-report.md` | 🟡 24h 待夜间执行 | 0.1.20 |
| T4.3 性能报告 | `docs/m1-perf-report.md` | 🟡 NFR-01 待基准机 | 0.1.20 |
| T4.4 验收清单 | `docs/m1-acceptance-checklist.md` | ✅ | 0.1.20 |
| T4.5 文档收口 | `README.md`、`docs/architecture-whitepaper-v0.1.md`、PRD 版本历史 | ✅ | 0.1.20 |

**四项 🟡 的共同点**：都不是实现缺口，而是需要现场条件（基准机 60Hz 会话、产线夜间窗口、
评审会签字）。每项在 `docs/m1-acceptance-checklist.md` 中都写明了复跑命令或签署方式。
