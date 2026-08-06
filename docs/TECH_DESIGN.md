# CodexQuotaTray 技术设计

## 当前技术基线

当前正式入口是 C# + WinUI 3，位于 `winui/`。旧 Rust/Win32 实现仅作为归档参考，不参与当前构建、测试、验证或发布。

协议生成基线由 [`schemas/CODEX_VERSION`](../schemas/CODEX_VERSION) 指定，对应 wire schema 位于该版本目录。运行时不读取 `schemas/`；这些文件只用于协议审计、升级 diff 和测试 fixture 校准。

## 分层架构

```text
Codex App Server
        │ UTF-8 JSONL over stdio
        ▼
Core/Protocol
        ▼ typed response + normalized quota
Core/Runtime ─── Core/Persistence
        │              │
        ├──── Core/Alerts
        ▼
Core/Presentation
        ▼
WinUI App / Views / Services / Interop / Themes
```

- `Core/Protocol`：App Server 进程、JSONL transport、请求路由、typed DTO、通知解析和额度规范化。
- `Core/Runtime`：连接生命周期、刷新协调、稀疏通知应用、当前状态和缓存提交。
- `Core/Persistence`：设置、额度缓存和提醒防重复状态。
- `Core/Presentation`：将归一化状态投影为 UI 状态和 ViewModel。
- `Core/Alerts`：可靠百分比阈值、周期重置和跨重启 at-most-once reducer。
- `App/Views`：WinUI 窗口和 XAML。
- `App/Services`：托盘、主题、通知和平台操作。
- `App/Interop`：Win32、AppWindow、DWM、显示器和 DPI 互操作。
- `App/Themes`：颜色、控件样式和主题资源。
- `Tests/FakeAppServer`：匿名 fixture 和确定性的离线验证。

UI 只消费 `AppUiState` 和 ViewModel，不解析 wire JSON，也不依赖协议 DTO。

## App Server 和只读边界

应用启动受控的 `codex app-server --stdio` 子进程，通过 UTF-8 JSONL 通信。连接使用唯一请求 ID 路由响应，持续排空 stdout/stderr，并通过超时、取消和受控进程树回收限制生命周期。

当前 outbound 边界只有：

- `initialize` request；
- `initialized` notification；
- `account/rateLimits/read` request。

服务端 `account/rateLimits/updated` 只作为入站通知处理。应用不构造登录、购买、重置卡消费或其他账户写请求。任何 outbound 扩展都必须先更新协议合同、隐私评审和离线序列测试。

原始响应、RPC error 正文和 stderr 原文不写日志或磁盘。额度窗口不依赖 `primary`、`secondary` 的固定周期含义；重置卡数量只接受 `availableCount`。

## Runtime 与状态

所有 Startup、Manual、Scheduled、CardOpened、Resume、NetworkRestored 和通知补读进入同一个刷新协调器。协调器保证单 in-flight、最小请求间隔和有界失败退避。

完整读取形成协议基线；稀疏通知只覆盖明确出现的字段。缺少可靠基线、通知溢出或通知不能形成安全独立快照时，Runtime 请求完整读取。失败保留最后有效状态，并由 Presentation 投影刷新中、警告、过期、离线或不可用表现。

归一化阶段：

- 优先使用非空 multi-bucket 数据，否则使用 legacy snapshot；
- 缺少 `usedPercent` 的窗口不进入有效列表；
- 越界百分比只在显示范围内 clamp，并标记为不可靠；
- opaque limit 标识只用于生成本地伪匿名键；
- reset-credit 归一化为 unavailable、empty、count-only、partial-details 或 complete-details。

## Persistence 与 Alerts

Production 和 Live Preview 使用不同数据目录；Demo Runtime 不写持久化数据。

持久化分为：

- settings：刷新、外观、缓存、提醒和启动偏好；
- quota cache：套餐、窗口百分比及可靠性、时长、重置时间、重置卡数量和最早已知到期时间；
- alert state：本地伪匿名窗口键、可靠百分比基线、已处理阈值和周期状态。

缓存不是 wire response archive，不保存 token、邮箱、原始 limit/reset-credit ID、原始响应或用户路径。JSON 文件有大小和条目上限，并使用受控替换提交。

提醒 reducer 只处理可靠百分比。首次快照和新启用阈值只建立基线；同一周期同一阈值最多提醒一次，确认新周期后重新激活。

## Production 与 Preview 身份

具体托盘 GUID 的唯一事实源是 [`TrayIconIdentity.cs`](../winui/src/CodexQuotaTray.App/Services/TrayIconIdentity.cs)，文档不复制其值。启动参数、单实例 key 和能力选择由 [`AppLaunchProfile.cs`](../winui/src/CodexQuotaTray.Core/Runtime/AppLaunchProfile.cs) 决定。

| 启动参数 | 数据源 | 数据位置 | 单实例与托盘身份 | 开机启动能力 |
| --- | --- | --- | --- | --- |
| 无参数 | Live Runtime | Production | Production | 允许 |
| `--demo` | Demo Runtime | 不持久化 | Preview | 不允许 |
| `--isolated-preview-data` | Live Runtime | Preview | Preview | 不允许 |
| `--demo --isolated-preview-data` | Demo Runtime | 不持久化 | Preview | 不允许 |

Production 与 Preview 使用不同单实例 key 和托盘身份，可以并存。Demo 始终使用 Preview 身份。Preview/Demo 不读取、写入或覆盖 Production 开机启动项，也不创建 Preview 启动项。

`--shutdown-existing` 根据同一启动配置选择目标身份，并在创建窗口、Runtime 或托盘前完成转发或退出。

## WinUI composition root

`App.xaml.cs` 负责组合运行配置、Runtime、主窗口、设置窗口、托盘和平台服务。Tooltip 的基础名称由当前 `TrayIconIdentity` 注入，再由 `TrayTooltipFormatter` 附加额度与状态摘要；动态更新不能硬编码 Production 名称。

产品版本的唯一声明位于 App 项目文件。`ProductVersion` 从入口程序集的 informational version 读取语义版本并移除构建元数据，供 About、诊断和 `initialize.clientInfo.version` 共用。

窗口、托盘 callback、Explorer 重建处理和应用生命周期相互隔离。窗口关闭通常只隐藏；显式退出或目标身份的 `--shutdown-existing` 才结束进程。

## 验证与发布

普通验证统一使用 [`scripts/verify-winui.ps1`](../scripts/verify-winui.ps1)。Quick、Full 和 Release 的准确行为由脚本本身定义；本文不复制日常命令。

真实账户和 Explorer 托盘 smoke 需要特定本机环境，所以不属于默认验证。安装器、签名和更广的 Windows/DPI 组合按发布阶段逐步检查，具体见 [Roadmap](ROADMAP.md) 和 [构建与发布](RELEASE.md)。
