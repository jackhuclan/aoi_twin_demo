# AOI 数字孪生 · 模型资产规范（T1.1，W1 冻结）

适用范围：M1 整机中模与各部件 GLB 资产、部件台账 JSON。校验由 `AoiTwin.AssetTools` 自动执行（T1.2），不合规资产不得入库。

## 1. PartId 命名规则

- 格式：`^[A-Z][A-Z0-9_]*$`（大写字母开头，仅大写字母/数字/下划线，≥ 2 字符）；
- 语义：`<模块>_<部件>`，如 `LIGHT_RING`、`GATE_AXIS_Z`；
- 保留 ID：`EQUIPMENT`（设备级消息）、`WAFER_CURRENT`（晶圆数据承载节点）——不得分配给普通部件；
- PartId 与 3D 节点一一对应，且与 PLC 侧部件台账一致（Raycaster 点选链路的唯一键）。

## 2. GLB 资产规范

| 项 | 规则 |
|---|---|
| 格式 | glTF 2.0 二进制（GLB），Draco 压缩可选（需在台账声明） |
| 面数预算 | 单部件 LOD1 ≤ 50,000 面；整机中模合计 ≤ 150,000 面；超预算资产校验工具报 E006 |
| 坐标系 | 米制；整机原点 = 设备安装基准面中心（X 右、Y 前、Z 上，右手系） |
| 原点约定 | 运动部件网格原点 = 该部件机械零位参考点（示教坐标 → 模型坐标换算依赖此约定，见 PRD FR-M2-03） |
| 命名 | LOD1：`<partId_snake>_l1.glb`；LOD2：`<partId_snake>_l2.glb`（如 `light_ring_l1.glb`） |
| LOD2 约束 | 隐藏线缆/门缝等细节；运动部件网格必须保留（仅简化几何），Transform 节点不得合并进静态合批 |
| 材质 | M1 统一 Blinn-Phong 级材质（PBR 预留给 M3 LOD0）；纹理尺寸 ≤ 2048² |

## 3. 部件台账 JSON（part-catalog.schema.json）

- 顶层：`assetVersion`（语义化版本）、`machine`（设备号）、`parts[]`；
- 每个部件：`partId` / `name` / `module` / `model` / `lod{ l1, l2 }` / `axes[]`（该部件受控轴名，与 PLC 轴命名一致）；
- 台账随资产包发布，版本号与 `assetVersion` 同步演进；
- 示例见 PRD-AOI-TWIN-M1 §5.6。

## 4. 校验规则（AssetTools 执行）

| 代码 | 级别 | 规则 |
|---|---|---|
| E001 | 错误 | 台账 JSON 无法解析或不符合 Schema |
| E002 | 错误 | partId 缺失或不符合命名规则 |
| E003 | 错误 | partId 重复 |
| E004 | 错误 | lod.l1 / lod.l2 引用的 GLB 文件不存在（相对 assets 根目录） |
| E005 | 错误 | GLB 无法加载（损坏/非 glTF 2.0） |
| E006 | 错误 | 面数超预算（单部件或整机合计） |
| E007 | 错误 | parts 为空 |

校验命令（W1 起资产交付自检）：

```bash
dotnet run --project src/AoiTwin.AssetTools -- validate assets/part-catalog.json assets/
```
