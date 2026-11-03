# AOI 数字孪生架构白皮书 v0.1（M1 收口版）

> 目标读者：参与 M2/M3 开发与三期「复用至 PL/EL 机台」的工程师。
> 本文说明 M1 冻结的类库边界、数据链路与扩展点。

## 1. 总体结构

```
┌────────────────────────────────────────────────────────────┐
│ AoiTwin.App（WPF 宿主：DI 组装、页签、主题、视图模型）        │
│   ├─ TwinSceneViewModel / CollectionMonitor / Replay / …   │
└──────┬──────────────────────┬───────────────────────────────┘
       │                      │
┌──────▼──────────┐   ┌───────▼───────────────────────────────┐
│ AoiTwin.Rendering│  │ AoiTwin.Adapters (+ .Mock)             │
│ 视口/相机/LOD/    │  │ 适配器基类（退避重连/健康状态机）         │
│ 点选/帧探针       │  │ PLC / Camera / Light / ResultQueue     │
└──────┬──────────┘   └───────┬───────────────────────────────┘
       │ 场景数据               │ ITwinMessage
┌──────▼──────────────────────▼───────────────────────────────┐
│ AoiTwin.Core                                                │
│  Bus(TwinTopic/四类消息/RealtimeHub) · Recording(.aotrec)    │
│  Catalog(PartCatalog) · Replay(ReplayEngine/批次索引)        │
│  Serialization(TwinJson 契约冻结)                            │
└─────────────────────────────────────────────────────────────┘
```

**依赖单向**：App → Rendering/Adapters → Core。Core 不引用任何 WPF/Helix 类型，
是三期跨机台复用的最小集（见 §5）。

## 2. 数据链路（采集方向）

```
设备 → 适配器(AdapterBase) ──MessageEmitted──▶ RealtimeHub ──Subscribe──▶ ① 录制器(.aotrec)
                                                │                          ② 采集监控(统计快照)
                                                └──────── M2: VisualViewModel(运动映射) ◀──
```

- **适配器**：`AdapterBase` 提供 1s→30s 指数退避重连、健康状态机（Connected/Reconnecting/…）、
  订阅方异常隔离（单源故障不影响其余通道，NFR-05）。协议实现只写传输层（`ITransport`），
  解析与映射（如 PLC i32 µm→mm）在适配器内完成。
- **RealtimeHub**：类型 + Topic 过滤的进程内总线，同步直调、快照查询、零丢包（NFR-03：0.49µs/条）。
- **消息契约**：`AxisSnapshot/StatusEvent/OpticsEvent/WaferResult` 四类 record，
  JSON 行为属 T1.0 冻结范围——**变更即变更 .aotrec 文件格式，必须走契约评审**（`TwinJson`）。

## 3. 数据链路（回放方向）

```
.aotrec ─▶ RecordingReader(逐帧 CRC + Footer SHA 校验) ─▶ ReplayEngine(变速/Seek/时钟重映射)
                │                                             │
                └─ WaferBatchIndex(批次区间)              ReplaySession ─▶ 逐条 Publish 回 Hub
```

- **录制**：分帧 20B 头（len/CRC/topic/typeId/ts）+ payload；Footer = magic + endTs + count +
  全文件 SHA-256。写入端加锁串行化（四路并发安全）；水位 85% 暂停；2h/班次滚动。
- **断电修复**：`Inspect`（扫描有效前缀）/`Repair`（重建 Footer）。
- **回放**：消息以 `with { Timestamp }` 重映射到回放时钟后回灌 Hub——**回放与实时共用同一套
  下游消费者**，M2 的运动映射无需区分数据来源。

## 4. 3D 渲染线（Rendering）

- **TwinViewport3D**：Z 向上米制、等轴测默认相机、地面网格 + 占地轮廓、每帧回调
  （M2 运动学插值直接取渲染节拍，不另起计时器）。
- **资产链路**：`GlbAssetLoader`（并行后台解析、部件级进度、异常带 PartId）→
  `SceneTreeBuilder`（部件节点 + `TranslateTransform3D` 运动挂点 + Mesh 挂 PartId 附加属性）。
- **LOD**：`LodPackage` 同持 L1 全保真与 L2 合批代理；**点选始终用 L1 几何**
  （`PartPicker` + 纯数学 Möller–Trumbore），LOD2 只是渲染形态——命中率与档位解耦。
  远档 Draw Call 10→5（−50%）。
- **点选**：视口点击 → `RayFrom` 射线 → `PartPicker` 最近命中 → 选中描边（反向壳，WPF 无
  stencil 的等效实现）→ 台账详情。
- **帧探针**：`FrameTimeSampler`（纯计算，120 帧滚动 FPS/P95/Max）+ `FrameTimeProbe`
  （CompositionTarget.Rendering 挂点、250ms 节流）——NFR-01/02 判据常驻 UI。

## 5. 三期复用边界（为 PL/EL 机台预留）

| 复用层 | 内容 | 复用方式 |
|--------|------|----------|
| Core | 消息契约、Hub、.aotrec、回放引擎 | 原样引用（零修改） |
| Adapters | 基类 + 协议骨架 | 新机台只新增传输实现 |
| Rendering | 视口/LOD/点选/探针 | 更换资产包（台账 + GLB）即可 |

**新增机台的接入工作量 = 一份台账 JSON + 一组 GLB + 若干 ITransport 实现**；
M2 的运动映射、M3 的分析都建立在 PartId 主键上，机台切换不影响上层。

## 6. M1 冻结的关键决策（M2/M3 不得隐式破坏）

1. 四类消息 + JSON 序列化行为（.aotrec 格式）——契约变更须评审；
2. 台账 schema（`part-catalog.schema.json`）——新增字段向后兼容；机械零位点在 M2 补 `pivot` 或由包围盒推算（asset-spec §5）；
3. 装配树约定：部件节点携带运动平移变换、Mesh 带 PartId——M2 只写 Offset，不重建树；
4. 点选集合固定 LOD1 几何；
5. 录制目录：现场班次 `assets/recordings/`（清单登记、不入 Git），Mock 演示写 `%LOCALAPPDATA%\AoiTwin\mock-recordings`；
6. 版本单一来源 `Directory.Build.props`；CPM 集中包管理（`Directory.Packages.props`）。

## 7. 已知边界与 M2 展望

- 适配器均为 Mock 传输；真实 PLC/SDK 协议在 T3.4 前补齐（风险项见实施计划 §8）；
- 稳定性：`tools/AoiTwin.Soak soak` 已实测 15min / 20min 两轮（丢帧 0、内存第 6 分钟走平、
  峰值增长 13MB，见 `docs/m1-stability-report.md`）；24h 全量夜间执行，判据由工具自动输出；
- 性能：NFR-02/03 均已实测达标，NFR-01 60fps 待基准机 60Hz 本地会话复测
  （本机远程会话裸渲染循环仅 ~32fps，无法作有效判据，见 `docs/m1-perf-report.md`）；
- M2 主题：Hub → VisualViewModel → 部件节点 Offset 的实时运动映射、状态徽标、缺陷热力叠加；
- M3 主题：批次时序档案、光衰预测、缺陷趋势（`MockScenario.decayChannel` 已埋素材）。
