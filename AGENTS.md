# AGENTS.md · 仓库组织规范

> 本文件是**项目结构与文件归属的唯一规范**，面向在本仓库工作的开发者与 AI 代理。
> 新增项目、新增文件、调整解决方案分层前先读本文件；与 README（面向使用者）分工不同：
> README 讲"怎么构建与使用"，本文件讲"东西放在哪里、为什么"。

---

## 1. 顶层布局

```
E:\aoi_twin_demo\
├─ AoiTwin.slnx              解决方案（唯一入口）
├─ .editorconfig             代码风格（解决方案级）
├─ Directory.Build.props     全解决方案共享元数据：版本单点、TreatWarningsAsErrors、Deterministic
├─ Directory.Packages.props  中央包管理（CPM）：所有 PackageReference 的版本在此
├─ nuget.config              包源配置
├─ .gitignore
├─ README.md                 使用说明（构建/运行/工具/测试矩阵）
├─ AGENTS.md                 本文件：组织规范
├─ src/                      源码项目（可交付产物）
├─ tests/                    测试项目（不发布）
├─ tools/                    工具项目（运维/验收/长跑，不随主程序发布）
├─ assets/                   运行时资产与配置（GLB、台账、配色、节流策略、标定、分析参数）
├─ docs/                     PRD、实施计划、验收清单、性能/稳定性报告、作业手册
└─ .github/workflows/        CI（build + 全量测试 + 失败注解）
```

**Solution Items = 解决方案级文件**，指"不属于任何单个项目、但整个解决方案共享"的文件。
它们必须留在仓库根目录（MSBuild 的 `Directory.Build.props` / `Directory.Packages.props`
只在目录树向上查找时才生效，挪走即失效），在 Visual Studio 中通过 `Solution Items` 文件夹集中呈现。

| 归入 Solution Items | 理由 |
|---|---|
| `.editorconfig` | 对所有项目生效的代码风格 |
| `README.md` / `AGENTS.md` | 仓库级说明，与具体项目无关 |
| `Directory.Build.props` / `Directory.Packages.props` | 全解决方案共享的编译元数据与包版本 |
| `nuget.config` / `.gitignore` | 仓库级工具链配置 |
| `AoiTwin.slnx` | 解决方案自身 |

---

## 2. 三个代码文件夹的用途与归属规则

### 2.1 `src/` — 源码项目

只放**会被交付/被引用的生产代码**。依赖方向严格单向：

```
AoiTwin.App ──► AoiTwin.Rendering ──► AoiTwin.Core
     │                │                    ▲
     ├──► AoiTwin.ViewModel ───────────────┤
     ├──► AoiTwin.Adapters ────────────────┤
     └──► AoiTwin.Adapters.Mock ───────────┘
AoiTwin.AssetTools ──► AoiTwin.Core
```

| 项目 | 目标框架 | 职责 | 禁止 |
|---|---|---|---|
| `AoiTwin.Core` | `net10.0` | 契约、总线、录制/回放、运动学、台账、**时序持久化与分析**（M3） | 引用任何 UI/Windows 类型 |
| `AoiTwin.ViewModel` | `net10.0` | 3D 专属视图模型、位姿引擎、上传/缺陷队列、配色 | 引用 `WindowsBase`/WPF（须可跨平台单测） |
| `AoiTwin.Adapters` | `net10.0` | 四路设备适配器基类与实现（真实协议接入层） | 直接操作渲染对象 |
| `AoiTwin.Adapters.Mock` | `net10.0` | Mock 传输与合成数据集 | 被生产代码反向依赖 |
| `AoiTwin.Rendering` | `net10.0-windows` | HelixToolkit 视口、场景装配、LOD、驱动器、FX | 引用 `AoiTwin.App` |
| `AoiTwin.App` | `net10.0-windows` (WinExe) | WPF 主程序、DI 组装、页面与导航 | 承载可复用的领域逻辑（应下沉到 Core/ViewModel） |
| `AoiTwin.AssetTools` | `net10.0` (console) | 资产校验、录制校验、占位资产生成、回灌 CLI | 引用 WPF |

**归属判定顺序**（新代码放哪里）：
1. 与设备/协议相关 → `Adapters`（真实）或 `Adapters.Mock`（模拟）；
2. 与 WPF/3D 对象相关 → `Rendering`；
3. 纯数据/算法/契约 → `Core`（不得依赖 UI）；
4. 只服务某个页面的状态编排 → `ViewModel` 或 `App/ViewModels`（仅当必须依赖 WPF 时留 App）；
5. 只在验收/运维时运行 → `tools/`。

### 2.2 `tests/` — 测试项目

命名固定 `AoiTwin.<被测层>.Tests`，一个被测项目对应一个测试工程（`App` 例外：`AoiTwin.App.Tests` 覆盖 UI 冒烟与 DI 装配）。

| 工程 | 覆盖 |
|---|---|
| `AoiTwin.Core.Tests` | 契约、总线/节流、录制、回放、台账、运动学 |
| `AoiTwin.ViewModel.Tests` | 位姿引擎、VM 聚合、配色、队列 |
| `AoiTwin.Rendering.Tests` | 场景/LOD/点选、驱动器、FX、性能探针 |
| `AoiTwin.Adapters.Tests` | 适配器状态机与 Mock 链路 |
| `AoiTwin.AssetTools.Tests` | 资产校验 E001–E007、录制校验 |
| `AoiTwin.App.Tests` | 联动服务、钻取、UI 冒烟、DI 装配一致性 |
| `AoiTwin.Regression.Tests` | 回放回归（**必须独立进程**：WPF 多 STA 用例同进程互相拖死） |
| `AoiTwin.Analytics.Tests` | M3 持久化/分析/预测回测/渲染增量 |

规则：
- 测试工程**不得**被 `src/` 引用；
- 测试数据写到 `Path.GetTempPath()` 下的临时目录并在用例结束清理，不写仓库；
- 涉及 WPF 的用例集中在 `Rendering.Tests`/`App.Tests`/`Regression.Tests`，且遵守既有的 STA 宿主约定。

### 2.3 `tools/` — 工具项目

放**人在现场/验收时运行、但不随产品发布**的可执行程序。

| 工具 | 用途 |
|---|---|
| `AoiTwin.Soak` | `soak` 采集浸泡、`bench` 性能基准、`soak-visual` 渲染长跑、`analytics` 分析链路离线验收（M3） |
| `soak-visual.cmd` | 72h 长跑一键入口（现场） |
| 新增工具 | 必须是 console/WinExe，可引用 `src/`，但 `src/` 不得引用 `tools/` |

---

## 3. 非代码目录

| 目录 | 放什么 | 不放什么 |
|---|---|---|
| `assets/` | 运行时**会被程序加载**的文件：`model/`、`part-catalog.json`、`throttle-policy.json`、`light-channel-colors.json`、`teach-calibration.json`、`analytics-options.json`、`recordings/manifest.md` | 文档、临时输出、实验脚本 |
| `docs/` | PRD（`docs/PRD/`）、实施计划（`PLAN-*.md`）、验收清单（`*-acceptance-checklist.md`）、性能/稳定性报告、作业手册 | 代码、构建产物 |
| `docs/_*.txt` | 工具原始输出（`bench`/`soak` 报告），**允许入库**作为验收物证 | 大型二进制 |
| `.github/workflows/` | CI 定义 | 本地脚本 |

约定：**报告与证据同批提交**。验收清单里引用的每个数字，必须能在 `docs/` 下找到对应原始输出。

---

## 4. Visual Studio 解决方案分层

解决方案必须与磁盘结构一致，分四组呈现，不允许项目平铺在根：

```
Solution 'AoiTwin'
├─ Solution Items/
│   ├─ .editorconfig
│   ├─ Directory.Build.props
│   ├─ Directory.Packages.props
│   ├─ nuget.config
│   ├─ README.md
│   └─ AGENTS.md
├─ src/
│   ├─ AoiTwin.Core
│   ├─ AoiTwin.ViewModel
│   ├─ AoiTwin.Adapters
│   ├─ AoiTwin.Adapters.Mock
│   ├─ AoiTwin.Rendering
│   ├─ AoiTwin.App
│   └─ AoiTwin.AssetTools
├─ tests/
│   └─ AoiTwin.*.Tests
└─ tools/
    └─ AoiTwin.Soak
```

`AoiTwin.slnx` 的对应写法（分组用 `<Folder>`，文件用 `<File>`，项目用 `<Project>`）：

```xml
<Solution>
  <Folder Name="/Solution Items/">
    <File Path=".editorconfig" />
    <File Path="README.md" />
    <File Path="AGENTS.md" />
  </Folder>
  <Folder Name="/src/">
    <Project Path="src/AoiTwin.Core/AoiTwin.Core.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/AoiTwin.Core.Tests/AoiTwin.Core.Tests.csproj" />
  </Folder>
  <Folder Name="/tools/">
    <Project Path="tools/AoiTwin.Soak/AoiTwin.Soak.csproj" />
  </Folder>
</Solution>
```

- **新增项目必须同时改两处**：磁盘目录 + `AoiTwin.slnx` 对应分组；漏改 slnx 会导致 CI 不编译该项目；
- 分组的名字固定为 `Solution Items` / `src` / `tests` / `tools`，不再新增一级分组（避免与磁盘结构分叉）；
- 项目内部的子文件夹按职责命名（`Bus/`、`Recording/`、`Dynamics/`、`Analytics/`…），不再引入解决方案级子分组。

---

## 5. 新增文件/项目的检查清单

- [ ] 位置符合 §2 的归属规则，命名符合 `AoiTwin.<层>[.<子域>]`；
- [ ] 目标框架与所在层一致（`Core`/`ViewModel`/`Adapters` 为 `net10.0`，渲染/App 为 `net10.0-windows`）；
- [ ] 依赖方向符合 §2.1 的单向图，没有反向引用；
- [ ] `AoiTwin.slnx` 已加入对应分组；
- [ ] 有对应测试（Core/ViewModel/Adapters 强制；渲染层可用验证清单 + 探针）；
- [ ] `dotnet build AoiTwin.slnx -c Release` 0 警告 0 错误、`dotnet test AoiTwin.slnx` 全绿；
- [ ] 文档/报告与代码同批提交，版本号只改 `Directory.Build.props` 一处；
- [ ] 提交信息用中文 Conventional Commits，尾注版本号（如 `feat(analytics): … (0.3.0)`）。

---

## 6. 与既有文档的关系

| 文档 | 关系 |
|---|---|
| `README.md` | 使用者视角：构建、运行、配置、测试矩阵 |
| `docs/asset-spec.md` | 资产包内部规范（GLB/台账/坐标），本文件不管资产细节 |
| `docs/PLAN-AOI-TWIN-M*-实施计划.md` | 任务分解与进度；本文件管"放哪里"，计划管"做什么" |
| `docs/architecture-whitepaper-v0.1.md` | 分层与依赖的技术论证；本文件是其工程落地约定 |
