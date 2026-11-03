# PRD · AOI 数字孪生三期（M3）：预测分析与优化

| 项 | 内容 |
|---|---|
| 文档编号 | PRD-AOI-TWIN-M3 |
| 版本 / 状态 | v1.0 / 待评审 |
| 上游文档 | PRD-AOI-TWIN-M2（契约承接方）、《方案B_3D场景结构图》《分期实施方案》 |
| 下游承接 | 多机位扩展 / PL·EL 机台复用（架构预留，另行立项） |
| 技术基线 | 同前两期；新增时序库（SQLite/Parquet 按天分区）、分析层插件框架、LOD 三档与合批终调 |

---

## 1. 背景与目标

### 1.1 背景
二期已实现 3D 与物理设备的实时镜像，且节流分发、VisualViewModel、FX 框架、钻取面板均已契约冻结。本三期在稳定的数据流上做"加法"：数据总线挂载持久化 Sink 沉淀时序数据，构建部件健康档案与首批预测模型，形成"预警 → 3D 可视化 → 维护建议"闭环；同步完成渲染性能与 LOD 体系终调，使系统达到产线长期运行与对外扩展标准。

### 1.2 三期目标
1. RealtimeHub 增加持久化 Sink（时序库按天分区），历史数据可回灌 3D；
2. 建立 PartId 主键的部件健康度档案，钻取面板升级健康度视图；
3. 落地首批预测模型（光源衰减、轴磨损、节拍劣化），预警经 `Alert` Topic 回流总线驱动 3D 可视化；
4. 渲染终调：LOD 三档（含 LOD0 特写 PBR）、静态合批、材质降级策略、GPU 上传队列调优；
5. 提供运维与对外扩展接口（数据导出、插件注册），产出架构白皮书。

### 1.3 非目标（本期不做）
- 多机位同屏（仅保证架构与 Topic 前缀机制就绪）；
- AI 深度学习模型（本期以规则/曲线拟合先行，模型接口可插拔替换）；
- 维护工单系统对接（仅提供导出接口）。

---

## 2. 用户故事

| 编号 | 角色 | 用户故事 | 验收条件 |
|---|---|---|---|
| US-M3-01 | 维护工程师 | 作为维护工程师，我想查看任一部件的历史曲线（轴负载/光源时长/节拍），以便分析劣化趋势 | 30 天内任意区间可查；查询响应 ≤ 3s |
| US-M3-02 | 维护工程师 | 作为维护工程师，我想在 3D 上按日期区间回放历史数据，以便复盘某天的异常过程 | 历史回灌复用回放引擎；按日期区间选择正确加载对应分区 |
| US-M3-03 | 维护工程师 | 作为维护工程师，我想系统提前告诉我环形光源即将光强不足，以便提前换灯 | 光源衰减预测提前量 ≥ 7 天（用二期数据回测验证） |
| US-M3-04 | 设备工程师 | 作为设备工程师，我想在轴重复定位偏差漂移时收到预警，以便安排检修避免停机 | 预警阈值可调；误报率经试运行评估并出具报告 |
| US-M3-05 | 工艺工程师 | 作为工艺工程师，我想看到单站节拍趋势与瓶颈工序定位，以便优化产线节拍 | 节拍趋势图 + 瓶颈 Top3 工序标注 |
| US-M3-06 | 设备工程师 | 作为设备工程师，我想在钻取面板看到部件健康度评分与构成，以便决定维护优先级 | 全部在册部件档案完整；评分规则透明可配置 |
| US-M3-07 | 产线操作员 | 作为操作员，我想在 3D 上直观看到哪个部件处于预警/报警状态（颜色升级 + 脉冲） | 预警触发 3D 状态变化；与 Alert Topic 同步 |
| US-M3-08 | 产线操作员 | 作为操作员，我想看到晶圆缺陷热力晕染，以便直观了解缺陷分布 | 热力层按需渲染；可开关 |
| US-M3-09 | IT/系统管理员 | 作为系统管理员，我想控制时序库的磁盘占用并配置清理策略 | 存储增长可预估；清理策略（保留天数）可配置且自动执行 |
| US-M3-10 | MES/YMS 开发者 | 作为下游系统开发者，我想导出健康度与预警数据，以便接入 MES/YMS | 只读导出接口（文件/API）文档完备 |
| US-M3-11 | 项目经理 | 作为项目经理，我要求新增数据源或预测模型不需修改 Hub 与渲染层代码 | 演示：以插件形式新增一个模型 + 一路数据源，零核心改动 |

---

## 3. 功能需求列表

| 编号 | 功能 | 优先级 | 需求描述 |
|---|---|---|---|
| FR-M3-01 | 时序持久化 Sink | P0 | Hub 订阅全量 Topic → 异步写时序库（SQLite/Parquet 按天分区）；轴负载曲线、光源通道时长与亮度、节拍、报警事件入库；写失败不阻塞实时链路（降级缓存重试） |
| FR-M3-02 | 存储治理 | P0 | 保留天数可配置、自动清理；磁盘水位保护；存储增长估算工具 |
| FR-M3-03 | 历史数据回灌 | P0 | 扩展回放引擎支持按日期区间从时序库回灌（与文件回放同一注入通道） |
| FR-M3-04 | 部件健康度档案 | P0 | PartId 主键：寿命计数（光源点亮时长/机械手循环次数/相机快门数）、报警频次统计、负载分布；档案随 Topic 持续更新 |
| FR-M3-05 | 健康度评分与视图 | P0 | 评分规则透明可配置（加权扣分制）；钻取面板升级：评分 + 趋势图 + 构成因子 |
| FR-M3-06 | 光源衰减预测 | P0 | 基于亮度-时长曲线拟合，预测光强跌至工艺下限日期；提前量 ≥ 7 天（回测验证） |
| FR-M3-07 | 轴磨损预警 | P0 | 重复定位偏差/电流特征漂移检测（滚动基线 + 偏差带）；阈值可配置 |
| FR-M3-08 | 节拍劣化分析 | P1 | 单站节拍趋势 + 瓶颈工序 Top3 定位 |
| FR-M3-09 | 预警回流与 3D 可视化 | P0 | 预警作为 `Alert` Topic 事件回注 Hub → 部件状态色升级 + 脉冲提示（复用二期 FX 框架，新增 FX Kind） |
| FR-M3-10 | 缺陷热力晕染 | P1 | Wafer_Current 上按缺陷密度生成热力覆盖层（GPU shader），按需渲染 |
| FR-M3-11 | UI3D KPI 标注层 | P1 | UI3D_Overlay：部件名 + 健康 KPI 屏幕投影（1s 节流，世界坐标→屏幕投影，与 WPF Adorner 层复用）；可一键关闭 |
| FR-M3-12 | LOD 三档与 PBR | P0 | 新增 LOD0 特写档（PBR 材质、螺栓/线缆/接口可见，相机距离 < 0.8m）；LOD1/2 Blinn-Phong 降级；按相机距离 + 书签双向平滑过渡 |
| FR-M3-13 | 静态合批 | P0 | 外壳/轨道/环境静态合批合并 Draw Call |
| FR-M3-14 | GPU 上传队列调优 | P1 | 上传优先级、分帧预算（单帧上传 ≤ N MB），避免纹理替换引起卡顿 |
| FR-M3-15 | 分析层插件框架 | P0 | `IAlertPredictor` 插件注册：新增模型 = 新增 DLL，零核心改动 |
| FR-M3-16 | 对外数据导出 | P1 | 只读导出接口：健康度档案、预警历史、时序片段（CSV/JSON）；供 MES/YMS 消费 |
| FR-M3-17 | 架构白皮书与接入指南 | P1 | 架构决策记录、部件台账接入指南、插件开发指南 |

---

## 4. 业务流程说明

### 4.1 时序沉淀流程
1. Hub 分发（二期逻辑不变）→ 持久化 Sink 作为普通订阅方接收全量消息；
2. Sink 异步批量写入当日分区（内存批量缓冲，≤ 5s 或 ≤ 5000 条刷盘）；
3. 写失败 → 本地重试队列（≤ 30min），超限降级告警（实时链路不受影响）；
4. 日切：跨天时刻滚动新分区，异步完成前一日分区压缩/转 Parquet。

### 4.2 分析与预警流程（核心闭环）
1. 分析调度器（默认 5min 周期 + 事件触发）拉取各插件 `Evaluate(profile)`；
2. 插件基于部件健康档案 + 时序窗口计算 → 产出 `AlertEvent`（Level: Warning/Critical）；
3. `AlertEvent` 以 **`Alert` Topic** 回注 Hub —— 与 PLC 报警同一条事件通道，渲染层把它当作普通状态事件消费；
4. 3D：FX 框架按 PartId 挂状态色升级 + 脉冲（Warning 黄 / Critical 红）；UI3D KPI 标注刷新；
5. 右侧钻取面板：预警部件出现预警卡片（原因因子 + 建议动作 + 关联趋势图）；
6. 对外：预警历史进入导出接口。

### 4.3 光源衰减预测模型（规则先行示例）
1. 统计 Light_Ring 各通道累计点亮时长与亮度设定值（来自 Optics Topic 沉淀）；
2. 定期记录实测光强（结果队列中的灰度基准片数据反演）→ 形成（时长，光强）样本；
3. 线性/指数拟合衰减曲线 → 外推至工艺下限 → 剩余天数 < 阈值（默认 7 天）触发 Warning，< 3 天触发 Critical；
4. 模型参数（下限阈值、提前量）随配方配置。

### 4.4 历史复盘流程
用户选择日期区间 → 从时序库加载对应分区 → 回放引擎注入 Hub（同一通道）→ 3D 按历史数据运动/变色 → 配合预警时间轴定位异常时刻。

### 4.5 性能终调流程
建立性能基线（一期/二期报告）→ LOD0/合批/材质降级逐项实施 → 每项附 A/B 帧率对比 → 达标后冻结渲染配置。

---

## 5. 接口与数据结构定义

> 承接 PRD-M1/M2 全部契约；本节仅定义**新增**部分。新增 Topic：`TwinTopic.Alert`（枚举扩展项）。

### 5.1 持久化 Sink 与存储

```csharp
namespace AoiTwin.Core.Persistence;

public interface IPersistenceSink : IAsyncDisposable   // 作为 Hub 订阅方注册
{
    Task WriteAsync(ITwinMessage message, CancellationToken ct);
    // 实现：内存批量缓冲 → 按天分区 SQLite/Parquet；失败进重试队列
}

public interface ITimeSeriesReader
{
    IAsyncEnumerable<ITwinMessage> QueryAsync(
        TwinTopic topic, DateTimeOffset from, DateTimeOffset to,
        string? partId = null, CancellationToken ct = default);
    long EstimateBytes(DateTimeOffset from, DateTimeOffset to);   // 存储增长估算
}
```

存储 Schema（按天分区，每 Topic 一表）：

| 表 | 主键/分区键 | 关键列 |
|---|---|---|
| motion_{yyyyMMdd} | (partId, axisName, ts) | position, velocity |
| status_{yyyyMMdd} | (partId, ts) | eventCode, severity, payloadJson |
| optics_{yyyyMMdd} | (deviceId, ts) | kind, channel, value |
| result_{yyyyMMdd} | (waferId, ts) | mapRef, tactMs, defectCount |
| alert_{yyyyMMdd} | (partId, ts) | code, level, message, model |

### 5.2 健康档案与预测插件

```csharp
namespace AoiTwin.Core.Analytics;

public sealed record PartHealthProfile(
    string PartId,
    LifetimeCounters Counters,        // 点亮时长/循环次数/快门数…
    AlarmStatistics   Alarms,         // 频次/最近报警/按代码分布
    LoadDistribution  Load);          // 负载直方图/偏差带

public interface IAlertPredictor     // 插件接口：新增模型 = 新增 DLL
{
    string Name { get; }             // 如 "LightDecayPredictor"
    AlertEvent? Evaluate(PartHealthProfile profile, ITimeSeriesReader series);
}

public sealed record AlertEvent(
    string PartId, AlertLevel Level, string Code, string Message,
    IReadOnlyDictionary<string, string> Evidence,   // 证据因子：剩余天数、偏差σ…
    DateTimeOffset Timestamp) : ITwinMessage;

public enum AlertLevel { Warning, Critical }
```

预警阈值/评分规则配置（随配方发布，热加载）：

```json
{
  "lightDecay": { "processFloorPct": 80, "warnDays": 7, "criticalDays": 3 },
  "axisWear":   { "sigmaBand": 3.0, "windowHours": 168, "warnSigma": 2.5 },
  "healthScore": { "lightLifetimeW": 0.4, "alarmFreqW": 0.3, "axisDriftW": 0.3 }
}
```

### 5.3 历史回灌与热力层

```csharp
namespace AoiTwin.Core.Replay;      // 扩展一期 IReplayEngine（原方法不变）

public interface IReplayEngine
{
    Task LoadTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct); // 新增：时序库回灌
}

namespace AoiTwin.Rendering.Fx;      // 扩展二期 PartFxKind
public enum PartFxKind { /* M2 既有 */ , AlertState, DefectHeatmap, KpiBadge }
```

---

## 6. 非功能需求

| 类别 | 编号 | 需求 |
|---|---|---|
| 性能 | NFR-19 | 典型工况 60fps（基准机）；低端核显 ≥ 30fps；LOD0 特写档 ≥ 45fps |
| 性能 | NFR-20 | Draw Call：LOD2 远景档较二期下降 ≥ 40%（合批后）；单帧 GPU 上传 ≤ 4MB（分帧预算） |
| 性能 | NFR-21 | 30 天数据任意区间查询响应 ≤ 3s（含索引）；Sink 写入延迟（内存缓冲到落盘）≤ 5s |
| 可靠性 | NFR-22 | 分析/存储链路任何故障不得影响实时镜像链路（进程隔离或异常边界 + 降级） |
| 可靠性 | NFR-23 | 连续 30 天运行（产线常态）：无崩溃、存储自动清理生效、内存稳定 |
| 安全 | NFR-24 | 时序库与导出接口沿用一期 ACL；导出数据脱敏选项（可去除产品相关字段） |
| 安全 | NFR-25 | 插件 DLL 需签名校验后方可加载（防未授权代码注入） |
| 可扩展性 | NFR-26 | 新增数据源/预测模型零核心改动（插件 + Topic 注册，US-M3-11 演示验证） |
| 可维护性 | NFR-27 | 预测阈值、健康评分权重、清理策略均为配置热加载 |

---

## 7. 验收标准（DoD）

1. **功能**：FR-M3-01 ~ 09、12、13、15（P0）通过验收测试；P1 完成或经批准列入后续迭代；
2. **预测有效性**：光源衰减预测用二期沉淀数据回测，提前量 ≥ 7 天且误报率报告出具；轴磨损预警经试运行（≥ 2 周）评估并调参记录；
3. **数据闭环**：30 天时序数据完整（抽样校验）；任意日期区间历史回灌复现正确；
4. **性能**：NFR-19 ~ 21 达标（A/B 对比报告：合批前后 Draw Call、LOD0 帧率、上传卡顿消除）；
5. **长稳**：30 天连续运行报告（NFR-22/23）；
6. **扩展性验证**：现场演示插件式新增 1 个预测模型 + 1 路数据源，Hub 与渲染层代码零改动（US-M3-11）；
7. 文档：架构白皮书、接入指南、插件开发指南评审通过。

---

## 8. 里程碑计划

| 里程碑 | 周期 | 内容 | 退出条件 |
|---|---|---|---|
| MS-M3-1 数据底座 | W1–W3 | 持久化 Sink + 存储治理 + 历史回灌；健康档案数据管道 | 30 天写入方案验证；回灌复现正确 |
| MS-M3-2 分析与预警 | W4–W6 | 健康度评分/视图 + 光源衰减/轴磨损/节拍三模型（回测调参）；Alert Topic 回流 + 3D 预警可视化 | 回测提前量 ≥ 7 天；预警 3D 联动演示通过 |
| MS-M3-3 渲染终调 | W7–W9 | LOD0 PBR + 静态合批 + 材质降级 + GPU 上传分帧；热力晕染 + UI3D KPI 标注 | NFR-19/20 A/B 报告达标 |
| MS-M3-4 验收与交付 | W10–W12 | 插件框架扩展性演示、对外导出接口、30 天长稳启动、文档与评审 | §7 全部满足，评审签字 |

**风险**：① 回测数据量不足（二期沉淀期短）→ 预警阈值先保守 + 明确标注置信度，试运行期持续校准；② 预测误报引发信任问题 → 评分规则透明可解释（证据因子展示）+ 阈值可调；③ LOD0 建模工作量 → 特写档仅覆盖高频钻取部件（相机/光源/载台），其余沿用 LOD1。

---

## 9. 三期收口与后续扩展就绪

三期结束时系统具备的扩展条件（对应《分期实施方案》§可扩展性架构）：
- **多机位**：场景树按设备子树实例化 + Topic 机台前缀机制已预留；
- **新机型复用（PL/EL）**：Hub、回放引擎、HelixToolkit 视口均为独立类库，资产包 + 台账映射即换机型；
- **分析能力持续演进**：`IAlertPredictor` 插件制，深度学习模型可无侵入替换规则模型；
- **下游集成**：MES/YMS 经只读导出接口消费健康度与预警数据。

多机位接入、深度学习模型、工单系统对接建议另行立项，不在本 PRD 范围内。
