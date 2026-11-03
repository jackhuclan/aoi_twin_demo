# AOI 数字孪生（M1：基础建模与数据采集）

面向化合物半导体 AOI 检测设备的数字孪生上位机。M1 交付：**3D 场景（建模/视口/LOD/点选）+
实时数据采集与录制 + 离线回放调试**，并为 M2（实时运动映射）与 M3（预测分析）预留扩展点。

## 功能总览（M1）

| 页签 | 能力 |
|------|------|
| 3D 场景 | GLB 资产后台预加载装配、相机书签（4 预置 + 自定义持久化，≤800ms 过渡）、LOD 两档（距离自动 + 手动锁定）、点选描边 + 部件台账、右侧停靠区（配方树/图像映射/直方图占位）、沉浸全屏 |
| 采集监控 | 四路数据源健康/速率/计数（PLC/相机/光源/结果队列）、Topic 吞吐、录制状态与磁盘水位告警、录制启停 |
| 回放调试 | .aotrec 文件加载、批次索引、0.5–8× 变速、Seek、逐帧步进 |
| 录制管理 | 目录扫描、文件校验（SHA-256 + 全帧 CRC）、断电文件修复、删除 |

## 构建与运行

要求：Windows 10 21H2+ / Windows 11、.NET 10 SDK。

```bash
dotnet build AoiTwin.slnx          # 0 警告 0 错误为合并门槛
dotnet test AoiTwin.slnx           # 全部测试（144+）
dotnet run --project src/AoiTwin.App
```

CI：GitHub Actions（windows-latest）执行 build + 全量测试，全绿为合并条件。

## 资产包

- 规范：`docs/asset-spec.md`（目录布局、命名、LOD、材质、坐标约定：米制、Z 向上）；
- 台账 Schema：`docs/part-catalog.schema.json`（PartId 规则、L1/L2 路径、运动轴）；
- 当前仓库内置程序化占位包（`assets/model/`），正式美术资产按规范替换即可，加载链路零改动；
- 校验工具：

```bash
dotnet run --project src/AoiTwin.AssetTools -- validate assets/part-catalog.json assets   # E001–E007
dotnet run --project src/AoiTwin.AssetTools -- gen-placeholder assets                     # 重新生成占位包
dotnet run --project src/AoiTwin.AssetTools -- verify <file.aotrec> [--repair]            # 录制文件校验/修复
```

## 数据采集与录制

- 录制格式 `.aotrec`：分帧（20B 头 + CRC32）+ Footer（SHA-256），断电可修复；
- 磁盘水位 85% 自动暂停、班次/2 小时滚动文件、环形缓冲防背压；
- 现场班次采集作业指导与登记清单：`docs/field-collection-runbook.md`（T3.4 待现场执行）；
- Mock 数据集：`AoiTwin.Adapters.Mock`（CI 全链路冒烟的数据源）。

## 验收与报告

| 文档 | 内容 |
|------|------|
| `docs/m1-perf-report.md` | NFR-01/02/03、LOD Draw Call、点选响应实测 |
| `docs/m1-stability-report.md` | 24h 稳定性浸泡（工具 + 实测数据） |
| `docs/m1-acceptance-checklist.md` | PRD §7 六项验收逐条打勾 + 证据 |
| `docs/PLAN-AOI-TWIN-M1-实施计划.md` | 任务分解与完成状态 |

## 架构速览

```
AoiTwin.Core            消息契约（TwinTopic/四类消息）、RealtimeHub、.aotrec 录制/读取、台账、回放引擎
AoiTwin.Adapters        适配器基类（退避重连/健康状态机）+ PLC/相机/光源/结果队列适配器
AoiTwin.Adapters.Mock   四路 Mock 传输与场景、Mock 数据集生成器
AoiTwin.Rendering       HelixToolkit 视口、相机书签、LOD、点选/描边、帧时间探针
AoiTwin.AssetTools      资产校验 / 录制校验 / 占位资产生成 CLI
AoiTwin.App             WPF 主程序（DI 组装、四个页签、深色主题）
tools/AoiTwin.Soak      浸泡稳定性 / 性能基准控制台
```

各层为独立类库、依赖单向（App → Rendering/Adapters → Core）——这是三期
「复用至 PL/EL 机台」的预留边界，详见 `docs/architecture-whitepaper-v0.1.md`。

## 提交约定

中文 Conventional Commits（`feat(core): …`），提交尾注版本号 `(0.1.x)`；
版本号统一由 `Directory.Build.props` 的 `<Version>` 管理。
