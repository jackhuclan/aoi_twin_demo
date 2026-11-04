# AOI 数字孪生（M2：实时数据映射与状态同步）

面向化合物半导体 AOI 检测设备的数字孪生上位机。

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M1 | 3D 场景（建模/视口/LOD/点选）+ 实时数据采集与录制 + 离线回放调试 | ✅ 已交付 |
| **M2** | **节流分发 + 3D 专属 VM + 运动/光学/晶圆/缺陷实时映射 + 四条联动链路 + FX 框架** | **本期** |
| M3 | 预测分析与工艺辅助（报警分级、持久化 Sink、异常预测） | 规划 |

## 功能总览

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
dotnet test AoiTwin.slnx           # 全部测试（255 例）
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

## 验收与报告

| 文档 | 内容 |
|------|------|
| `docs/m2-perf-report.md` | M2 NFR-11/12/13/17 实测 + 口径 + 已知偏差 + 现场待测清单 |
| `docs/m2-acceptance-checklist.md` | M2 PRD §7 七项验收逐条打勾 + 证据 + M3 冻结清单 |
| `docs/m2-contract-baseline.md` | M1 冻结契约核对 + M2 新增契约 |
| `docs/PLAN-AOI-TWIN-M2-实施计划.md` | M2 任务分解与完成状态 |
| `docs/m1-perf-report.md` / `m1-stability-report.md` / `m1-acceptance-checklist.md` | M1 基线（性能 / 24h 浸泡 / 验收） |

## 架构速览

```
AoiTwin.Core            消息契约（TwinTopic/四类消息）、RealtimeHub + 节流、.aotrec 录制/读取、
                        台账、回放引擎、运动学（示教换算 / 轴插值）
AoiTwin.ViewModel       3D 专属 VM（net10.0 纯数据、不依赖 WindowsBase）：位姿引擎、双缓冲快照、
                        上传/缺陷队列、配色、钻取与缺陷列表
AoiTwin.Adapters        适配器基类（退避重连/健康状态机）+ PLC/相机/光源/结果队列适配器
AoiTwin.Adapters.Mock   四路 Mock 传输与场景、Mock 数据集生成器
AoiTwin.Rendering       HelixToolkit 视口、相机书签与跟随、LOD、点选/描边、
                        Dynamics（运动/光学/视野锥/纹理/点云/装载）、Fx（描边/脉冲/粒子）、帧时间探针
AoiTwin.AssetTools      资产校验 / 录制校验 / 占位资产生成 CLI
AoiTwin.App             WPF 主程序（DI 组装、四个主页面、右侧六页签、深色主题）
tools/AoiTwin.Soak      浸泡稳定性 / 性能基准控制台
```

各层为独立类库、依赖单向（App → Rendering / ViewModel / Adapters → Core）——这是三期
「复用至 PL/EL 机台」的预留边界，详见 `docs/architecture-whitepaper-v0.1.md`。

## 测试工程

| 工程 | 用例数 | 覆盖 |
|---|---|---|
| `AoiTwin.Core.Tests` | 77 | 契约、Hub 与节流、录制、回放、台账、运动学 |
| `AoiTwin.Rendering.Tests` | 85 | 场景/LOD/点选、六个驱动器、FX、跟随、**性能探针** |
| `AoiTwin.ViewModel.Tests` | 42 | 位姿引擎、VM 聚合、配色、缺陷列表、装载状态 |
| `AoiTwin.Adapters.Tests` | 23 | 四路适配器与 Mock 链路 |
| `AoiTwin.AssetTools.Tests` | 13 | 资产校验 E001–E007 |
| `AoiTwin.App.Tests` | 14 | 联动服务、钻取、UI 冒烟（端到端）、DI 装配一致性 |
| `AoiTwin.Regression.Tests` | 1 | 回放回归（FR-M2-03~07，**独立进程**：WPF 多 STA 用例同进程会互相拖死） |

## 提交约定

中文 Conventional Commits（`feat(core): …`），提交尾注版本号 `(0.2.x)`；
版本号统一由 `Directory.Build.props` 的 `<Version>` 管理。
