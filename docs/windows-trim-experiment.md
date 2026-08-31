# Windows Trim 可行性实验

日期：2026-08-31

分支：`codex/windows-trim-experiment`

基线：从 `codex/windows-thin-installer` 当前 HEAD `1afc49f6652b875036680188f8221e88c85f9f45` 创建；本实验未合入 `main`。

## 结论

结论为 **C. `NOT READY`**。

`PublishTrimmed=true` 确实带来明显体积收益，但当前 trimmed Preview 已复现两类功能性问题：

1. 传统 WinUI `{Binding}` 对应的 ViewModel 成员被裁剪，额度卡片无法正常投影；
2. 点击刷新时在 WinRT 的 `PropertyChangedEventHandler` 代理调用链中发生 `InvalidCastException`，进程退出。

此外，项目自己的 JSON 反射序列化仍产生真实 `IL2026` 风险，Windows SDK/WinRT 运行时产生 35 条 `IL2081` 泛型 ABI 警告。仅增加 ViewModel rooting 没有修复刷新崩溃，因此当前不建议在 Production Release 启用 Trim。

正式 Release 行为保持不变：项目仍为 `PublishTrimmed=false`。本轮没有提交业务代码修复；临时 descriptor 只放在被忽略的 `target/trim-experiment` 下，用于验证 rooting 是否足够，未进入提交。

## 基线与实验参数

当前相关发布参数来自 `windows/src/CodexQuotaTray.App/CodexQuotaTray.App.csproj` 和 `windows/scripts/publish-winui.ps1`；正式发布步骤仍以 [Windows README](../windows/README.md) 和 [Release 文档](RELEASE.md) 为准。

| 参数 | 未 Trim Release | Trim 实验 |
| --- | --- | --- |
| Target framework | `net10.0-windows10.0.26100.0` | 相同 |
| RID / platform | `win-x64` / `x64` | 相同 |
| `SelfContained` | `true` | `true` |
| `WindowsAppSDKSelfContained` | `false` | `false` |
| `PublishTrimmed` | `false` | `true` |
| `PublishSingleFile` | `false` | `false` |
| `PublishReadyToRun` | `false` | `false` |
| Native AOT | 未启用 | 未启用 |

Trim analyzer 的有效实验先以 Trim 参数 restore，再使用 `--no-restore` publish；同时设置 `EnableTrimAnalyzer=true`、`SuppressTrimAnalysisWarnings=false`、`TrimmerSingleWarn=false`。`TreatWarningsAsErrors=false` 仅作为本地调查命令行参数，未修改项目配置。没有使用 `NoWarn`。

## 体积对比

大小按字节和 MiB（`1 MiB = 1,048,576 bytes`）统计。未 Trim 的 portable ZIP 和 Inno setup 使用同一轮 Release payload；Trim 版本使用同样的打包脚本/排除 PDB 规则和 Windows App Runtime 外部安装器来源。

| 产物 | 未 Trim | Trim | 节省 | 节省比例 |
| --- | ---: | ---: | ---: | ---: |
| publish directory | 166,315,543 B / 158.61 MiB，248 文件 | 71,208,327 B / 67.91 MiB，91 文件 | 95,107,216 B | 57.18% |
| portable ZIP | 67,151,922 B / 64.04 MiB | 30,733,587 B / 29.31 MiB | 36,418,335 B | 54.23% |
| Inno setup | 48,560,143 B / 46.31 MiB | 24,231,207 B / 23.11 MiB | 24,328,936 B | 50.10% |
| portable stage（ZIP 前） | 166,028,971 B / 158.34 MiB，252 文件 | 70,935,276 B / 67.65 MiB，95 文件 | 95,093,695 B | 57.28% |

主要体积变化如下；ONNX/DirectML 等原生依赖基本没有因 Trim 变化，因此收益主要来自托管 .NET/WinRT 程序集裁剪。

| 文件 | 未 Trim | Trim | 变化 |
| --- | ---: | ---: | ---: |
| `Microsoft.Windows.SDK.NET.dll` | 26,341,408 B | 227,328 B | -26,114,080 B |
| `System.Private.CoreLib.dll` | 16,033,576 B | 3,098,624 B | -12,934,952 B |
| `System.Private.Xml.dll` | 7,788,328 B | 移除 | -7,788,328 B |
| `Microsoft.WinUI.dll` | 7,336,760 B | 595,456 B | -6,741,304 B |
| `System.Linq.Expressions.dll` | 3,639,120 B | 38,912 B | -3,600,208 B |
| `System.Data.Common.dll` | 2,770,728 B | 101,888 B | -2,668,840 B |
| `System.Security.Cryptography.dll` | 2,549,544 B | 216,064 B | -2,333,480 B |
| `System.Private.DataContractSerialization.dll` | 2,062,160 B | 移除 | -2,062,160 B |
| `System.Text.Json.dll` | 1,890,088 B | 252,416 B | -1,637,672 B |
| `Microsoft.InteractiveExperiences.Projection.dll` | 1,620,792 B | 87,552 B | -1,533,240 B |

未 Trim 与 Trim 版本中 `onnxruntime.dll` 均为 21,659,280 B，`DirectML.dll` 均为 18,700,224 B，`e_sqlite3.dll` 均为 1,978,880 B。

冷启动用本地 Preview、`--demo --isolated-preview-data` 测量“进程启动到 `MainWindowHandle` 非零”，不是首像素或首个可用面板：3 次结果未 Trim 为 685/626/572 ms，中位数 **626 ms**；Trim 为 823/769/821 ms，中位数 **821 ms**。该指标下 Trim 没有显示启动收益，反而慢约 195 ms；样本量小，不能作为性能结论。

## Trim warning 清单与判断

### `IL2026`：应用代码的 System.Text.Json 反射序列化

详细分析输出中有 16 条独立 `IL2026`。普通 publish 输出因编译器/Trim analysis 两阶段重复显示为 32 行；它们不是 32 个不同问题。所有问题都来自没有 `JsonTypeInfo`/`JsonSerializerContext` 的 `JsonSerializer` overload：

| 位置 | 用途 | 判断与后续方向 |
| --- | --- | --- |
| `Core/Persistence/JsonFileStore.cs:29` | 泛型持久化 load | 真问题；改为传入对应 `JsonTypeInfo<T>` 或拆分为 source-generated 类型入口 |
| `Core/Persistence/JsonFileStore.cs:42` | 泛型持久化 save | 同上 |
| `Core/Persistence/JsonFileStore.cs:84` | `SaveWithCommitAsync` 泛型 save | 同上 |
| `Core/Auth/OAuthCredentialStore.cs:41` | DPAPI credential load | 真问题；为 credential DTO 加 source generation |
| `Core/Auth/OAuthCredentialStore.cs:53` | DPAPI credential save | 同上 |
| `Core/Auth/OAuthClient.cs:405` | `object` 请求体 JSON | 真问题；需要使用命名请求 DTO 或显式 `JsonTypeInfo`，不能继续依赖 object 运行时类型发现 |
| `Core/Auth/OAuthAppServerClient.cs:234,237` | reset-credit 请求 JSON primitive | 真问题；可改为显式 JSON value 或 source-generated 请求模型 |
| `Core/Protocol/JsonLineRpcConnection.cs:210` | 泛型 JSON-RPC write | 真问题；需要 RPC 消息类型的 source-generated metadata |
| `Core/Protocol/CodexAppServerClient.cs:115` | initialize result deserialize | 真问题；为协议 DTO 加 source-generated metadata |
| `Core/Protocol/CodexAppServerClient.cs:164` | rate limits deserialize | 同上 |
| `Core/Protocol/CodexAppServerClient.cs:365` | notification deserialize | 同上 |
| `Core/TokenUsage/TokenUsageSyncServer.cs:463` | quota response serialize | 真问题；为同步协议 DTO 加 source-generated metadata |
| `Core/TokenUsage/TokenUsageSyncServer.cs:470` | token snapshot response body | 真问题；匿名类型应替换为命名 DTO 或显式 metadata |
| `App/Services/LanDiagnosticBuffer.cs:256` | LAN diagnostic state save | 真问题；为 state DTO 加 source-generated metadata |
| `App/Services/LanDiagnosticBuffer.cs:309` | LAN diagnostic state load | 同上 |

Trim state-machine 的部分诊断把 `TokenUsageSyncServer` 的第二处调用映射到生成状态机的旧行号（日志中出现 `:399`）；源代码中的实际调用是 `:470`。`OAuthAppServerClient` 的两处 primitive 调用在详细 Trim 分析中都映射到 `ToResetCredit` 的同一源位置，但仍对应两个独立调用。

项目没有 Newtonsoft.Json、Gson 等另一套反射序列化依赖。当前 JSON DOM 的 `JsonDocument`/`JsonElement.TryGetProperty` 手工解析不是 Trim warning，但不能据此推断所有 JSON 路径已经安全，因为上述序列化路径仍存在。

结论：这是应用拥有的真实 Trim 适配工作，不应通过 `NoWarn` 处理。最小可行方向是先建立覆盖持久化、RPC、OAuth、LAN 和 token sync 的 source-generated JSON metadata，再让泛型 store/RPC API 接收 `JsonTypeInfo<T>`；本轮不做这项横跨多个协议的改造。

### `IL2081`：WinRT.Runtime / Windows SDK 泛型 ABI

详细输出共有 **35 条** `IL2081`，全部来自生成的 `WinRT.Runtime`/Windows SDK ABI fallback。每条都指出泛型 ABI 参数没有满足 `PublicParameterlessConstructor` 的动态访问标注。按 generated type/fallback 归类如下，数量合计 35：

| generated ABI family | 条数 |
| --- | ---: |
| `IAsyncActionWithProgress<TProgress>` 及其 `Methods` | 2 |
| `IAsyncOperation<TResult>` 及其 `Methods` | 2 |
| `IAsyncOperationWithProgress<TResult,TProgress>` 的 result/progress fallback 与 `Methods` | 4 |
| `IMapChangedEventArgs<K>` 及其 `Methods` | 2 |
| `IObservableMap<K,V>` 及其 `Methods` | 4 |
| `IObservableVector<T>` 及其 `Methods` | 2 |
| `IDictionary<K,V>` 及其 `Methods` | 4 |
| `IEnumerable<T>` | 1 |
| `IEnumerator<T>` 及其 methods | 2 |
| `IList<T>` 及其 methods | 2 |
| `IReadOnlyDictionary<K,V>` 及其 methods | 4 |
| `IReadOnlyList<T>` | 1 |
| `IVectorViewMethods<T>` | 1 |
| `KeyValuePair<K,V>` | 2 |
| `KeyValuePairMethods<K,V>` | 2 |

默认 single-warning 输出将这类问题汇总为两条 `IL2104`：

- `Microsoft.Windows.SDK.NET.dll : Assembly 'Microsoft.Windows.SDK.NET' produced trim warnings`
- `WinRT.Runtime.dll : Assembly 'WinRT.Runtime' produced trim warnings`

判断：这是第三方/SDK runtime compatibility 问题，不是某个应用 DTO 缺少一个 `DynamicDependency`。对 WinRT generated ABI 做全局 rooting 或 `NoWarn` 都不能证明安全，当前应视为生产 Trim blocker，需要上游 Windows App SDK/WinRT runtime 的 Trim 支持或经过验证的官方配置。

### 代码与依赖风险审查

- 未发现 production source 中的 `Type.GetType`、`Activator.CreateInstance`、`Assembly.Load*`、`GetMethod`、`GetProperty`、`GetTypes`、`dynamic`、`MakeGenericMethod` 等动态发现/实例化模式；`Assembly.GetEntryAssembly()` 仅用于版本和元数据读取。
- `JsonFileStore<T>`、`JsonLineRpcConnection.WriteAsync<T>` 等泛型本身是静态调用，但其 JSON options overload 仍会触发 `IL2026`，不能把“使用泛型”误判为 Trim 安全。
- SQLite 使用 `Microsoft.Data.Sqlite`/SQLitePCL raw SQL、reader 和显式 mapping，没有发现 ORM convention/reflection mapping；`e_sqlite3.dll` 在两版中未裁剪。仍需保留实际数据库 smoke。
- ZXing 只在 `TokenUsageQrCodeGenerator` 通过 `BarcodeWriterPixelData` 静态使用；Trim publish 仍包含 `zxing.dll`，没有发现动态 decoder discovery warning。QR/ZXing 仍需实际路径 smoke。
- OAuth 响应主要是 DOM 手工解析，DPAPI credential store 和 OAuth request serialization 仍在 `IL2026` 清单中；真实 OAuth/credential 路径未在本轮使用真实账户执行。
- AppNotification、WinUI、CsWinRT projections 使用静态 API，但属于 WinRT runtime 风险面；demo Preview 不会覆盖 live AppNotification 注册/通知投递。
- tray/native interop 主要是静态 P/Invoke/native callback；未发现 reflection discovery warning。官方 tray smoke 因已有 Production 实例而安全拒绝，未强行关闭该实例。
- updater 使用手工 JSON DOM、SHA-256 校验和 `Process.Start`；Production-only updater 初始化没有在 demo Preview 中执行，且没有为此改变 Production 配置。
- CommunityToolkit.Mvvm 的 `ObservableObject`/command 链在 trimmed WinUI 运行时的 `PropertyChangedEventHandler` 代理处实际崩溃，说明仅通过静态 source scan 不能排除生成 delegate/WinRT interop 问题。

## XAML `{Binding}` 审查

源代码中主要传统 Binding 使用位置统计如下：

| View | `{Binding}` 数量 | `x:Bind` / `x:DataType` 情况 |
| --- | ---: | --- |
| `MainWindow.xaml` | 10 | 无 `x:Bind`、无 `x:DataType` |
| `QuotaView.xaml` | 17 | 有 `x:DataType="core:QuotaWindowItemViewModel"`，主要内容仍是 `{Binding}` |
| `SettingsWindow.xaml` | 84 | 无 `x:Bind`、无 `x:DataType` |
| `TokenUsageView.xaml` | 15 | 有 `x:DataType="presentation:TokenHeatmapCell"`，主要内容仍是 `{Binding}` |
| `QuotaProgressVisual.xaml` | 0 | 1 个 `x:Bind` |

主要 Binding 覆盖标题、状态、refresh command、quota windows、5h/7d reset 文本、Settings 的 OAuth/数据源/刷新/配对/主题/通知/更新命令，以及 Token Usage 汇总和 heatmap。它们正是不能只靠“应用能启动”来判定的成员集合。

对 baseline/trimmed 程序集元数据进行比较发现，未 root 的 Trim 版本至少裁掉了以下绑定相关成员：

- `MainViewModel`：`HasPlanBadge`、`ShowContent` 等；
- `QuotaWindowItemViewModel`：`Name`、`Tone`、`PercentText`、`DisplayPercent`、`ResetRelative`、`ResetAt`、`ProgressValue`、`IsStale` 等；
- `SettingsViewModel`：大量页面属性以及 `RefreshQuotaCommand`、`CheckForWindowsUpdatesCommand`、`DownloadWindowsUpdateCommand`、`CopyDiagnosticsCommand` 等；
- `TokenUsageViewModel`：`TodayTokens`、`Last7DaysTokens`、`Last30DaysTokens`、`LifetimeTokens`、`PeakDailyTokens`、`LongestStreak` 等。

实验中用临时 `preserve="all"` descriptor root 住五个主要 ViewModel。它能恢复程序集元数据中的公开成员，但仍未恢复额度卡片，也未修复 refresh 的 WinRT delegate 崩溃。因此当前证据不足以支持添加一组“看起来够用”的 root；大范围把 `{Binding}` 迁移为 `x:Bind` 也不在本轮范围内。

## Smoke 结果

### 已执行

| 场景 | 未 Trim | Trim | 结果 |
| --- | --- | --- | --- |
| Preview 启动 | 正常 | 正常 | 两者均能创建窗口 |
| 主额度面板 | 正常显示额度卡片 | 窗口可见但 5h/7d 卡片未正确投影 | Trim 失败 |
| 5h/7d 显示 | 可见 | 不可见/空白 | Trim 失败 |
| Token Usage 页面 | 可打开 | 可打开，统计标签可见；demo 没有真实 token 数据 | 页面导航通过，数据路径仍需人工/离线 fixture |
| Preview Refresh | 64% → 63%，进程保持运行 | 点击后 `InvalidCastException`，进程退出 | Trim 失败 |
| 临时 ViewModel rooting 后 | 不适用 | 仍然同样崩溃 | rooting 不足 |
| 关闭/重启 | 正常 | Alt+F4 关闭后可再次启动 Preview | 通过 |
| 冷启动 handle-ready 中位数 | 626 ms | 821 ms | 未显示收益 |

Trim refresh 的异常路径为：`MainViewModel.set_IsRefreshing` → `ObservableObject.OnPropertyChanged` → `ABI.System.ComponentModel.PropertyChangedEventHandler.NativeDelegateWrapper.Invoke` → `InvalidCastException (0x80004002)`。这不是 warning-only 的理论风险，而是实际 trimmed Preview 行为。

### 未执行或需人工 smoke

demo Preview 不会覆盖所有 live/Production-only 路径，且仓库规则禁止为了 smoke 关闭现有 Production 进程。因此以下项目需在隔离环境、明确的人工批准和非真实敏感数据条件下复测：

- tray callback、右键菜单和显示/隐藏切换；
- Settings 全部页面及所有 Binding/Command；
- Codex app-server 初始化、rate limits、notification 和刷新失败保留旧状态；
- Token SQLite 读取、写入、迁移与 LAN sync；
- OAuth 登录、DPAPI credential 保存/恢复和退出登录；
- QR 生成/扫描相关 ZXing 路径；
- AppNotification 注册、通知构建、投递和注销；
- 通知注册与提醒去重；
- 自动更新初始化、下载/校验/启动安装器；
- 主题切换、关闭/重启和实例恢复。

官方 `windows/scripts/test-winui-tray.ps1` 已尝试执行 trimmed executable，但预检发现已有 Production 实例后直接拒绝，未启动或终止它。这项不是 Trim 通过，而是安全阻断。

## 实际代码修改

没有生产代码修改。实验只使用了命令行 Trim 参数和被 Git 忽略的 `target/trim-experiment` 临时输出/descriptor：

- 未修改 `csproj` 中的 `PublishTrimmed=false`；
- 未添加 `DynamicDependency`、annotation、descriptor 到正式项目；
- 未改写 XAML Binding；
- 未改动 JSON、SQLite、OAuth、通知、tray、updater 或业务架构；
- 未使用 `NoWarn`，也未启用 Native AOT、SingleFile、ReadyToRun 或 framework-dependent 发布。

原因是：应用侧 JSON 需要一组跨协议的 source-generation 设计，而 WinRT `IL2081` 和实际 delegate 崩溃属于不能由一个安全的小 patch 证明解决的第三方/interop 风险。此时添加局部 rooting 会制造“warning 变少但运行仍不安全”的假象。

## 后续如果要正式开启 Trim

建议按以下门槛推进：

1. 所有持久化、RPC、OAuth、LAN diagnostic、token sync JSON 入口改用 `JsonSerializerContext`/`JsonTypeInfo`；泛型 store 和 RPC writer 需要显式接收类型 metadata，匿名请求体改为命名 DTO。
2. 新增 DTO 时同时登记 source-generated metadata，并测试缺失字段、未知字段、malformed 输入和默认值，不能让 malformed 数据变成零值。
3. 传统 `{Binding}` 要么在明确的 ViewModel/type 范围内做最小 rooting，要么按页面逐步转换为 `x:Bind`；每次新增属性、命令、converter 或 DataTemplate 都要检查 Trim 后元数据和实际 UI，不做全项目迁移。
4. 新增或升级 NuGet 时检查其 Trim/NativeAOT 兼容声明、generated COM/WinRT 代码和动态发现行为；尤其要验证 Windows App SDK、WinRT.Runtime、通知 API、SQLite、ZXing 等真实调用路径。
5. 将 `SuppressTrimAnalysisWarnings=false`、`TrimmerSingleWarn=false` 的 analyzer publish 长期保留在 CI，初期作为 advisory；不使用 `NoWarn`。只有当 warning 全部归零或有可审计的上游解释，并且 Preview/离线/人工 live smoke 都通过后，才考虑把 CI 门禁化。
6. 每次涉及 JSON/XAML/NuGet/reflection 的新功能都应运行 trimmed publish、检查 IL2xxx、验证 binding/command/property 元数据，并执行与功能相关的 trimmed smoke。

在当前代码和依赖组合下，建议保留 analyzer CI（先 advisory），但继续使用未 Trim 的 Production Release。Trim 的体积收益是真实的，却不足以抵消目前已经复现的 UI 和 interop 运行时故障。
