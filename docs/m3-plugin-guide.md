# M3 插件开发指南（IAlertPredictor）

> 面向：想给数字孪生加一个预测模型（或把规则模型换成深度学习）的算法/工艺工程师。
> 目标：**不改 Hub、不改渲染层、不改主程序**，只交一个 DLL。

---

## 1. 能力边界

插件能做：读分析上下文（健康档案 + 时序片段）、产出预警（含证据因子与置信度）、参与健康评分。
插件不能做：直接操作 Hub / 渲染对象 / 文件系统（没有句柄，也不该有）。

这样设计的原因：分析层一旦能直接推消息或改 3D，就会出现"某个插件把 UI 线程拖死"的
不可诊断故障。**预警只经 `TwinTopic.Alert` 回流**，与 PLC 报警走同一条通道。

---

## 2. 接口

```csharp
public interface IAlertPredictor
{
    string Name { get; }                 // 唯一，重名注册会抛异常
    string Version => "1.0";
    IReadOnlyList<AlertFinding> Analyze(IAnalyticsContext context);
}
```

上下文提供：

| 成员 | 说明 |
|---|---|
| `Now` | 分析基准时刻（回测时由调用方指定，**不要用 DateTime.Now**） |
| `Profiles` | 各部件健康档案（行程/报警/亮灯时长/节拍） |
| `Options` | 全局阈值与权重（含 `Window`） |
| `LightSamples(window)` | 光源样本（按时间升序） |
| `AxisSamples(window)` | 轴样本（位置 µm + 速度） |
| `TactSamples(window)` | 节拍样本（站点从 WaferId 解析） |

产出：

```csharp
new AlertFinding(
    PartId: "LIGHT_RING",
    Kind: AlertKind.Custom,          // 自定义模型用 Custom，无需改枚举
    Severity: Severity.Warning,
    Score: 42,                       // 0–100，越大越紧急
    Summary: "振动 RMS 超过阈值",
    Factors: [ new AlertFactor("振动 RMS", 12.4, "mm/s", "来源 VIBRATION_RMS") ],
    HorizonDays: null,
    Confidence: 0.6);
```

**规则**：
1. 没有证据因子的预警不允许发布——现场无法解释的预警会被直接忽略，等于没做；
2. `HorizonDays` / `Confidence` 能算就算，算不出就留 null，不要编；
3. `Analyze` 不得抛异常（注册表会兜底，但模型自身缺陷会被记为 `PredictorRun.Error`）；
4. 单次 `Analyze` 应在 200ms 内完成（调度周期 5min，但别把一帧的时间花在这）。

---

## 3. 最小示例

```csharp
using AoiTwin.Core.Analytics;
using AoiTwin.Core.Bus;

public sealed class VibrationPredictor : IAlertPredictor
{
    public string Name => "vibration";

    public IReadOnlyList<AlertFinding> Analyze(IAnalyticsContext context)
    {
        var findings = new List<AlertFinding>();
        foreach (var profile in context.Profiles)
        {
            if (!profile.Extra.TryGetValue("vibrationRms", out var rms) || rms < 10)
                continue;

            findings.Add(new AlertFinding(
                profile.PartId, AlertKind.Custom, Severity.Warning,
                Math.Min(100, rms * 3),
                $"振动 RMS {rms:F1} 超过 10",
                [new AlertFactor("振动 RMS", rms, "mm/s")]));
        }
        return findings;
    }
}
```

> 当前 `PartHealthProfile` 未提供 `Extra` 字典时，可改用 `context.AxisSamples` / `LightSamples` 自行计算；
> 自定义数据源可通过 `StatusEvent.Payload` 承载（如 `VIBRATION_RMS`），由既有适配器发布。

---

## 4. 打包与签名

1. 目标框架与宿主一致：`net10.0`（分析层不依赖 WPF）；
2. 只引用 `AoiTwin.Core`（禁止引用 `AoiTwin.Rendering` / `AoiTwin.App`）；
3. 强名签名（公司密钥），记下公钥 token：

```csharp
var token = PluginLoader.TokenOf(AssemblyName.GetAssemblyName("MyPredictor.dll"));
// 形如 "9a4b...c1"（十六进制小写）
```

4. 现场配置白名单后加载：

```csharp
var registry = new PredictorRegistry();
var report = PluginLoader.LoadPredictors(
    registry,
    @"C:\AoiTwin\plugins",
    new PluginLoadOptions(["<公钥 token>"]));
// report.Loaded / Rejected / Messages 逐条打日志
```

**安全默认**：`AllowedPublicKeyTokens` 为空 → 全部拒绝（含无强名 DLL）。
这是刻意的：现场机器上放一个 dll 就能跑任意代码，必须显式授权。

---

## 5. 本地调试（无设备）

```bash
# 用合成/回灌数据跑一遍你的模型，不需要设备
dotnet run --project tools/AoiTwin.Soak -- analytics 30 <storeRoot> <report> --options assets/analytics-options.json
```

报告里 `options.*` 会写出本次生效的阈值——调参后对比两份报告，就能判断是「模型变了」还是「口径变了」。

或写单元测试：

```csharp
var store = new FileTimeSeriesStore(tempDir);
store.Append(/* 构造已知规律的样本 */);
var options = new AnalyticsOptions();   // 或 AnalyticsOptionsProvider(文件).Current
var context = new AnalyticsContext(now, profiles, options, store, TimeSpan.FromDays(30));
var findings = new MyPredictor().Analyze(context);
```

建议至少覆盖：正常不报、异常必报、样本不足不报（**假阴性/假阳性都要有用例**）。

---

## 6. 上线检查

- [ ] 本地测试三件套（正常/异常/样本不足）通过；
- [ ] `Name` 唯一、`Version` 已填；
- [ ] 每条 finding 带因子，能一句话解释"为什么报"；
- [ ] 与既有三个模型同时运行 1 小时，无 CPU 尖峰（调度器串行执行，慢模型会推迟其他模型）；
- [ ] 现场用 §4 流程验证：无签名被拒、签名可加载；
- [ ] 在 `docs/m3-acceptance-checklist.md` 的扩展性演示里登记一次（模型名/版本/效果）。
