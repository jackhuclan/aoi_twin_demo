# AOI 数字孪生（M3：预测分析与工艺辅助）

面向化合物半导体 AOI 检测设备的数字孪生上位机。

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M1 | 3D 场景（建模/视口/LOD/点选）+ 实时数据采集与录制 + 离线回放调试 | ✅ 已交付 |
| M2 | 节流分发 + 3D 专属 VM + 运动/光学/晶圆/缺陷实时映射 + 四条联动链路 + FX 框架 | ✅ 已交付 |
| **M3** | **时序持久化 + 健康评分 + 三预测模型 + 预警回流 3D + 渲染终调 + 对外导出** | **本期：离线交付完成（43 例全绿），真机项待 180 分钟窗口** |

## 功能总览

### M3（预测分析与工艺辅助）

| 能力 | 说明 |
|------|------|
| 时序持久化 | 五类消息落盘（`<root>/<yyyyMMdd>.jsonl`，UTC 天分区）；Sink 满 5000 条或 5s 刷盘、失败重试 ≤30min、缓冲超限丢最旧——**永不反压实时链路**（NFR-22）；`ITimeSeriesStore` 抽象，可换 SQLite 而上层零改动 |
| 存储治理 | 保留 30 天自动清理 + 磁盘水位应急清理（最近 1 天必留）+ 增长估算 |
| 历史回灌 | `.aotrec → 时序库`（逐帧 CRC 校验）；任意日期区间回放到 3D（0.5–8×、Seek、时间戳重映射），与文件回放共用同一 Hub 注入通道 |
| 健康档案 | 行程 / 报警 / 亮灯时长 / 节拍随 Topic 持续聚合；Welford 单遍统计、按消息时间戳（乱序安全） |
| 健康评分 | 扣分制（100 起扣、每条扣分都带证据、权重可热加载），四档 Good/Watch/Warning/Critical |
| 光源衰减 | 线性回归 + R² + 95% 预测区间 → 剩余天数；**回测提前量 ≥7 天已达标**（`Light_decay_gives_seven_day_lead_time_on_backtest_window`） |
| 轴磨损 | 保持段位置离散度 z 分数 + 漂移 µm/天；⚠️ 代理量（总线无指令/反馈双值），真机需交叉验证 |
| 节拍劣化 | 单站节拍趋势（末段 vs 基线）+ 瓶颈工序 Top3 |
| 预警回流 3D | `Alert` Topic → 部件状态色（Warning 黄静态 / Critical 红呼吸）；恢复时发 `Info` 自动退色；去重键 + 升级即发 + 30min 重复抑制 |
| 插件框架 | `IAlertPredictor` 注册即生效；外部 DLL 走强名公钥白名单（**默认全拒**）+ 可卸载 AssemblyLoadContext |
| 渲染终调 | LOD0 三档（opt-in：资产缺失自动退回两档）、静态合批（按运动组+颜色，保留 UV/法线）、上传分帧 4MB/帧、缺陷热力 ≤8 Draw Call、UI3D KPI 1s 节流 |
| 对外导出 | 健康档案 / 预警历史 / 时序片段导出 CSV·JSON；三角色 ACL（Viewer/Engineer/Admin）+ 脱敏 + 原子写 |

> 模型口径与真机验证点见 `docs/m3-field-verification.md`；实现对照与实测见 `docs/m3-implementation-status.md`。

### M2（实时映射与联动）

| 能力 | 说明 |
|------|------|
| 节流分发 | 轴数据 200ms 按键合并（`PartId\|AxisName` 各自保留最新）、状态/参数事件直推、整片结果逐片推送；策略文件热加载，坏配置保旧值 |
| 运动同步 | 示教坐标 → 模型坐标（µm→m、方向/Scale/Sign/Offset），2–5Hz 轴数据经环形缓冲线性插值到 60fps；LOD2 代理按运动组随动 |
| 光学变色 | Light_Ring 8 通道按 PRD §5.5 配色表变色（叠加视觉，不改基线材质）；Light_Coax 亮度 → 自发光强度 |
| 视野锥 | Camera_LineScan 棱锥随扫描区域变化，扫描进度线按片推进 |
| Wafer Map | 整片 `WriteableBitmap` 纹理替换（几何与材质引用零重建），0=未测/1=OK/2=NG/3..255 灰度 |
| 缺陷点云 | 按等级分池、512 点/块批量提交（O(增量)），支持等级配色与 3D 高亮 |
| 四条联动 | 部件钻取（≤200ms）/ 右键诊断跳转（按相机-光源-轴-通用分流）/ 缺陷 2D↔3D 双向 + 原图窗口（≤100ms）/ 跟随载台 |
| FX 按需渲染 | 空 FX 时不进 Draw Call；首批：选中描边、报警脉冲、传输粒子流 |
| 装载状态 | Track 晶圆占位 + LoadPort Cassette 25 槽，状态事件驱动 3D（1s 节流） |

### M1（基础建模与采集）

| 页签 | 能力 |
|------|------|
| 3D 场景 | GLB 资产后台预加载装配、相机书签（4 预置 + 自定义持久化，≤800ms 过渡）、LOD 两档（距离自动 + 手动锁定）、点选描边 + 部件台账、右侧停靠区（配方树 / 缺陷列表 / 直方图 / 部件详情 / 诊断）、沉浸全屏 |
| 采集监控 | 四路数据源健康/速率/计数（PLC/相机/光源/结果队列）、Topic 吞吐、录制状态与磁盘水位告警、录制启停 |
| 回放调试 | .aotrec 文件加载、批次索引、0.5–8× 变速、Seek、逐帧步进 |
| 录制管理 | 目录扫描、文件校验（SHA-256 + 全帧 CRC）、断电文件修复、删除 |

## 构建与运行

要求：Windows 10 21H2+ / Windows 11、.NET 10 SDK。

```bash
dotnet build AoiTwin.slnx          # 0 警告 0 错误为合并门槛
dotnet test AoiTwin.slnx           # 全部测试（298 例）
dotnet run --project src/AoiTwin.App
```

CI：GitHub Actions（windows-latest）执行 build + 全量测试，全绿为合并条件；
测试失败时把失败用例名与断言首行打成 **check-run 注解**（运行日志需鉴权，注解公开可读）。

## 配置文件（均在 `assets/`，改文件免重编译）

| 文件 | 内容 | 生效方式 |
|---|---|---|
| `throttle-policy.json` | 各 Topic 的节流窗口与合并键 | 热加载（150ms 防抖） |
| `light-channel-colors.json` | 光源通道八色 + 缺陷等级配色 + 状态色（含 M3 预留 `Warning`/`Critical`） | 热加载 |
| `teach-calibration.json` | 示教坐标 → 模型坐标标定（PartId/AxisName/Direction/Scale/Sign/Offset） | 启动加载 |
| `part-catalog.json` | 部件台账（PartId、LOD 路径、运动轴） | 启动加载 |
| `analytics-options.json` | 分析阈值与健康评分权重（光源额定值/预警线、轴 σ 与漂移、节拍劣化、扣分权重） | 每轮分析读取；坏配置保旧值 |

> `analytics-options.json` 只需写想改的字段，其余走代码默认值；允许 `//` 注释与尾随逗号
> （现场手改容错）。**注意 `light.ratedValue` 必须按现场标称值填**：留 0 会把窗口首个样本当额定，
> 衰减预测会自动变得「永远还够用」。字段含义与调参流程见 `docs/m3-field-verification.md` §2。

## 资产包

- 规范：`docs/asset-spec.md`（目录布局、命名、LOD、材质、坐标约定：米制、Z 向上）；
- 台账 Schema：`docs/part-catalog.schema.json`（PartId 规则、L1/L2 路径、运动轴）；
- 当前仓库内置程序化占位包（`assets/model/`，16 部件 / 11 轴），正式美术资产按规范替换即可，加载链路零改动；
- 校验工具：

```bash
dotnet run --project src/AoiTwin.AssetTools -- validate assets/part-catalog.json assets   # E001–E007
dotnet run --project src/AoiTwin.AssetTools -- gen-placeholder assets                     # 重新生成占位包
dotnet run --project src/AoiTwin.AssetTools -- verify <file.aotrec> [--repair]            # 录制文件校验/修复
```

## 数据采集与录制

- 录制格式 `.aotrec`：分帧（20B 头 + CRC32）+ Footer（SHA-256），断电可修复；
- 磁盘水位 85% 自动暂停、班次/2 小时滚动文件、环形缓冲防背压；
- 现场班次采集作业指导：`docs/field-collection-runbook.md`；登记清单：`assets/recordings/manifest.md`；
- Mock 数据集：`AoiTwin.Adapters.Mock`（CI 全链路冒烟与回放回归的数据源）。

## 长跑（TA.18）

渲染侧长跑要开窗才量得到帧率（WPF 帧节拍来自窗口合成），故 `soak-visual` 会建一个屏幕外窗口，
把「六路同步驱动器 + 报警/粒子 FX + 跟随相机」按主程序的装配顺序挂进帧循环：

```bash
tools\soak-visual.cmd                                                        # 72h（现场窗口 B）
AoiTwin.Soak soak-visual 30 docs\_soak-visual-30m.txt --sample-sec 30 --warmup-min 3   # 离线自检
```

输出三件套：报告（判据 + 抽稀明细）、`.progress`（30s 心跳，崩溃物证）、`.csv`（内存/帧率曲线）。
判据与判读规则见 `docs/m2-stability-report.md`。

## 分析与离线验收（M3）

没有设备时，`analytics` 子命令是唯一能证明「写入 → 治理 → 回查 → 预测 → 导出」整条链路可运行的物证：
它合成 N 天时序、经 `PersistenceSink` 真落盘、逐日跑存储治理、再做 30 天窗口分析 + 评分 + 导出。

```bash
# 离线加速 30 天（通常 < 1s，报告自带判据与 result=PASS/FAIL）
AoiTwin.Soak analytics 30 "$TEMP/aoi-m3-store" docs/_m3-analytics-30d.txt --options assets/analytics-options.json
```

不传 `--options` 时用代码默认值；两种跑法的报告第一屏都会写出本次生效的阈值
（`options.light/axis/tact`）——换阈值会换结论，证据必须自带口径。

报告要点：`recordsWritten` / `partitions`（≤31）/ `sinkHealthy` / `finding.*`（等级·评分·提前量·摘要）
/ `export.*`（CSV 与 JSON 双份）。

`analytics` 不替代真机 30 天长稳：内存与分区是有界性的结构验证，真机数据量与 IO 压力仍需现场
（判据与流程见 `docs/m3-field-verification.md` §8）。

## 验收与报告

| 文档 | 内容 |
|------|------|
| `docs/m3-implementation-status.md` | M3 TB.0–TB.20 逐条对照 + 离线实测 + 三处偏离 + 五个已知缺口 |
| `docs/m3-acceptance-checklist.md` | M3 PRD §7 七项验收逐条打勾 + 证据 + 误报率模板（待签字） |
| `docs/m3-field-verification.md` | M3 真机验证点 11 节：方法 / 判据 / 不通过处置 |
| `docs/m3-plugin-guide.md` | 预测插件开发指南：接口、示例、签名白名单、上线检查单 |
| `docs/m2-perf-report.md` | M2 NFR-11/12/13/17 实测 + 口径 + 已知偏差 + 现场待测清单 |
| `docs/m2-stability-report.md` | M2 长跑（TA.18）：判据、`soak-visual` 用法、现场流程、判读规则、30 分钟自检 |
| `docs/m2-acceptance-checklist.md` | M2 PRD §7 七项验收逐条打勾 + 证据 + M3 冻结清单 |
| `docs/m2-contract-baseline.md` | M1 冻结契约核对 + M2 新增契约 |
| `docs/PLAN-AOI-TWIN-M3-实施计划.md` / `…-M2-…` | 任务分解与执行进度 |
| `docs/m1-perf-report.md` / `m1-stability-report.md` / `m1-acceptance-checklist.md` | M1 基线（性能 / 24h 浸泡 / 验收） |

## 架构速览

```
AoiTwin.Core            消息契约（TwinTopic/五类消息）、RealtimeHub + 节流、.aotrec 录制/读取、
                        台账、回放引擎、运动学（示教换算 / 轴插值）、
                        Persistence（时序库/Sink/治理/回灌/健康档案）、
                        Analytics（预警契约/预测器注册表/三模型/评分/插件/导出）
AoiTwin.ViewModel       3D 专属 VM（net10.0 纯数据、不依赖 WindowsBase）：位姿引擎、双缓冲快照、
                        上传/缺陷队列、配色、钻取与缺陷列表
AoiTwin.Adapters        适配器基类（退避重连/健康状态机）+ PLC/相机/光源/结果队列适配器
AoiTwin.Adapters.Mock   四路 Mock 传输与场景、Mock 数据集生成器
AoiTwin.Rendering       HelixToolkit 视口、相机书签与跟随、LOD（含 LOD0 三档）、点选/描边、静态合批、
                        上传分帧、Dynamics（运动/光学/视野锥/纹理/点云/装载）、
                        Fx（描边/脉冲/粒子/预警状态/缺陷热力）、UI3D KPI、帧时间探针
AoiTwin.AssetTools      资产校验 / 录制校验 / 占位资产生成 CLI
AoiTwin.App             WPF 主程序（DI 组装、四个主页面、右侧六页签、深色主题）
tools/AoiTwin.Soak      稳定性/性能控制台：soak、bench、soak-visual、analytics（M3 离线加速验收）
```

各层为独立类库、依赖单向（App → Rendering / ViewModel / Adapters → Core）——这是三期
「复用至 PL/EL 机台」的预留边界，详见 `docs/architecture-whitepaper-v0.1.md`
（M3 增量为该文 §9）。

## 测试工程

| 工程 | 用例数 | 覆盖 |
|---|---|---|
| `AoiTwin.Core.Tests` | 77 | 契约（Topic 只增不改）、Hub 与节流、录制、回放、台账、运动学 |
| `AoiTwin.Rendering.Tests` | 85 | 场景/LOD/点选、六个驱动器、FX、跟随、**性能探针** |
| `AoiTwin.Analytics.Tests` | 43 | **M3**：时序落盘/治理/回灌、三模型回测、评分、预警去重升级恢复、插件签名、静态合批/上传分帧/热力/投影、导出 ACL、参数文件、30 天加速长稳、扩展性结构断言 |
| `AoiTwin.ViewModel.Tests` | 42 | 位姿引擎、VM 聚合、配色、缺陷列表、装载状态 |
| `AoiTwin.Adapters.Tests` | 23 | 四路适配器与 Mock 链路 |
| `AoiTwin.AssetTools.Tests` | 13 | 资产校验 E001–E007 |
| `AoiTwin.App.Tests` | 14 | 联动服务、钻取、UI 冒烟（端到端）、DI 装配一致性 |
| `AoiTwin.Regression.Tests` | 1 | 回放回归（FR-M2-03~07，**独立进程**：WPF 多 STA 用例同进程会互相拖死） |

合计 **298 例**。`AoiTwin.Analytics.Tests` 同时引用 Core / ViewModel / Rendering，
并可无头驱动渲染侧纯逻辑（合批、LOD 选择、上传预算、投影、热力分档）。

## 提交约定

中文 Conventional Commits（`feat(core): …`），提交尾注版本号 `(0.3.x)`；
版本号统一由 `Directory.Build.props` 的 `<Version>` 管理。
