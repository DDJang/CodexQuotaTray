# WinUI 3 渐进迁移

状态：第四阶段 0.3.3 正式交付候选已实现；Rust 源码仍保留作回归基线
目标 SDK：.NET `10.0.302`、Windows App SDK `2.2.0`  
更新日期：2026-07-19

## 1. 已确认事实

- Rust 0.2.0 已把 App Server transport、JSON-RPC dispatcher、协议解析、额度归一化、状态 reducer、刷新、提醒和持久化拆分为独立模块。
- Windows host 仍集中承担大量绘制、托盘、窗口生命周期、通知、启动项和系统事件职责，是迁移风险最高的边界。
- 正式用户数据位于 `%LOCALAPPDATA%\CodexQuotaTray`，JSON 使用 camelCase；`alert-state.json` 包含 `schemaVersion: 1`。
- 正式产品的目标仍是一个 C# 主进程；受控的 `codex app-server` 子进程不构成 Rust/C# 双产品进程。
- WinUI 3 自包含发布的目录体积和运行内存显著高于当前 Rust 构建，因此 20 MB 包体和 35 MB 工作集只作为对照数据，替换前必须基于实测重新审批。

## 2. 第一阶段已实现

第一阶段只验证展示与 Windows host，不迁移业务：

- `CodexQuotaTray.Core` 提供无 WinUI 依赖的 `AppUiState`、额度窗口、五种重置卡状态、颜色语义、`MainViewModel`、窗口定位和 backdrop 降级策略。
- `CodexQuotaTray.App` 使用构造注入的 `DemoStateProvider`。它不启动 App Server，也不访问正式配置目录。
- 主窗口使用 WinUI 原生布局、`ItemsRepeater`、`ProgressBar` 和主题资源；宽度为 420 DIP，高度按 0–3 个额度窗口计算。
- Light、Dark、HighContrast 使用相同语义资源键；关键状态同时有文字，颜色不是唯一表达。
- backdrop 顺序为 Desktop Acrylic → Mica → 不透明主题背景；关闭透明效果或高对比度时直接使用不透明背景。
- 独立 message-only HWND 承载托盘消息，使用独立 GUID 和 `NOTIFYICON_VERSION_4`；左键每次只切换一次，Explorer 重启后重新添加。
- `Shell_NotifyIconGetRect` 成功时按真实图标和任务栏边缘定位，失败时退回光标显示器工作区右下角。
- 原型使用独立程序集名与单实例键。普通启动隐藏，`--demo` 显示；关闭窗口只隐藏，显式退出才结束。

## 3. Rust 到 C# 的迁移映射

| Rust 当前模块 | C# 目标边界 | 阶段 |
| --- | --- | --- |
| `protocol.rs` | `ProtocolModels` App Server DTO | 2，已实现只读字段 |
| `quota.rs` | `QuotaNormalizer` + `QuotaViewProjector` | 2，已实现 |
| `json_rpc.rs` | `JsonLineRpcConnection` | 2，已实现 |
| `app_server.rs`, `supervisor.rs` | `CodexAppServerProcess` + Windows Job Object | 2，已实现 |
| `compatibility.rs` | capability result + 脱敏 CLI 版本 | 2，已实现 |
| `state.rs` | `QuotaRuntimeService` 内存 snapshot | 3，统一运行时 |
| `ui_model.rs` | `QuotaViewProjector` + `MainViewModel` | 2，已实现 |
| `refresh.rs` | 唯一 `RefreshCoordinator` | 3，已实现 |
| `alerts.rs`, `alert_store.rs` | `QuotaAlertReducer` 与原子持久化 | 3，已实现 |
| `settings.rs`, `cache.rs` | camelCase 设置/缓存适配器 | 3，已实现 |
| `windows_tray.rs` | WinUI Window + 独立托盘/系统服务 | 1–3 |
| `windows_visuals.rs` | XAML、主题资源与原生控件 | 1 |

这里的映射是迁移设计，不代表服务端协议保证。第二、三阶段必须继续以 Rust fixture 和已生成 schema 为行为基线。

## 4. 隐私与兼容门禁

- 0.3.3 默认读写兼容的 `%LOCALAPPDATA%\CodexQuotaTray`。开发 smoke 可使用 `--isolated-preview-data`，不会触碰正式数据。
- 第二阶段前，不创建仅占位的 App Server、持久化或提醒实现。
- C# 持久化必须逐字段兼容现有 camelCase 格式，并保留未知字段的安全降级行为。
- 不持久化 token、邮箱、账户 ID、原始 limit ID、重置卡 ID 或原始协议响应。
- `alert-state.json` 的 `schemaVersion`、本地伪匿名标识和 at-most-once 顺序在替换前必须有跨实现 fixture 证明。

## 5. 后续阶段与替换门禁

### 第二阶段：真实只读额度（已实现）

- 移植 stdio 子进程和不带 `jsonrpc` 字段的 JSONL dispatcher；
- 以请求 ID 路由并发响应，完整处理错误、通知、超时和 EOF；
- 实现 reset-credit 五态和真实 `AppUiState` 投影；本阶段明确不订阅稀疏通知。
- fake App Server 覆盖 malformed JSON、未知通知、method-not-found、超时、EOF 和正常关闭。

默认运行只发送 `initialize → initialized → account/rateLimits/read`。右上角刷新复用 session，并由单一 semaphore 抑制并发；失败保留最后成功数据。退出时取消会话、关闭 stdin、等待 3 秒，超时才结束进程树，Windows Job Object 防止 shim 后代残留。

诊断只输出版本 token、能力布尔值、匿名计数、刷新 UTC 和错误枚举；不输出 CLI 绝对路径、stderr 原文、原始响应、token、邮箱、账户/limit/credit ID。当前阶段仍不写正式用户目录。

### 第三阶段：运行时与用户状态（已实现代码基线）

- 唯一 `QuotaRuntimeService` 负责 refresh、push merge、stale、提醒与状态投影，UI 不再拥有独立刷新路径；
- 设置、额度缓存和 `schemaVersion: 1` alert state 使用 camelCase、64 KiB 限制及原子替换；清额度缓存不会删除提醒防重复状态；
- 阈值提醒按 `previous > threshold && current <= threshold`，先保存再请求系统通知；稳定 ID 仅保存 SHA-256 本地伪匿名标识；
- 设置页、独立预览启动项、系统恢复与 IP Helper 网络恢复事件已接入；卸载和 KeepUserData 仍属于第四阶段安装器工作。

### 第四阶段：正式交付候选（已实现）

- 使用 unpackaged、folder-based、self-contained x64 发布，正式程序集名为 `codex-quota-tray-gui.exe`；
- 保持 `SelfContained=true`、`WindowsAppSDKSelfContained=true`、`PublishSingleFile=false`、`PublishTrimmed=false`；
- Inno 已改为封装完整 WinUI 发布目录，并保留原 `AppId`、安装路径、`--shutdown-existing` 和 KeepUserData 行为；
- Windows 10/11、DPI、多显示器、安装升级卸载、隐私和长期资源测试全部通过后，才替换正式入口。

## 6. 正式发布仍需人工门禁

- Rust 源码尚未删除，并继续参与所有回归检查；这不影响安装器使用 C# 正式入口。
- 预览版通知继续使用 `NIF_INFO`，不承诺操作系统一定展示；状态保存成功但通知失败时允许漏发，以防重复为优先。
- 自动测试验证纯逻辑和主题资源完整性；100%–200% DPI、透明效果开关、高对比度、Explorer 重启和视觉截图仍需要在对应 Windows 环境人工 smoke。
- 本地安装器未签名；公开分发仍需 Authenticode/Trusted Signing 与 Windows 10/11 完整矩阵。

## 7. 本机测量

2026-07-19 在 Windows 11 x64、当前桌面缩放下进行短时采样：

- Release `--demo` 冷启动后 3 秒工作集约 178 MiB；执行一次静态刷新后观测到约 198 MiB；
- Debug `--demo` 冷启动后 3 秒工作集约 180 MiB；
- folder-based self-contained x64 发布目录为 518 个文件、218.74 MiB；
- 首个 `--demo` 进程可直接显示窗口；静态刷新、禁用重复触发、关闭只隐藏均通过 smoke。

这些是短时单机样本，不是性能承诺，也不能替代 Windows 10/11、各 DPI 和长期资源测试。结果明确高于 Rust 版原指标，必须在第四阶段替换评审前重新设定并批准预算。

### 第二阶段 capability smoke

- 本机当前可直接执行的 CLI 已是 `0.145.0-alpha.18`，不是计划基线 `0.144.5`。
- `initialize` 成功，但首次 `account/rateLimits/read` 返回 JSON-RPC `-32600`；同一路径上的 Rust 0.144.5 基线探针得到相同结果。
- 临时生成的 0.145 alpha schema 仍声明该方法可用且 `params` 为可选 null，因此没有凭假设改变生产请求形状，也没有执行剩余 49 次读取。
- 冷启动错误路径工作集约 171 MiB；修复了失败清理时先等待 pipe reader 导致 UI 永久停留“正在连接”的问题。现在先关闭 stdin/回收进程，stdout EOF 后再等待 dispatcher。
- 错误路径空闲 5 分钟采样：工作集 `171.78 → 172.29 MiB`，句柄 `915 → 906`，线程 `26 → 22`，CPU 累计约 `7.56 s`；没有持续内存、句柄或线程增长。
- 窗口显隐状态机完成 100 次 `hidden → visible → hidden` 离线循环；该路径不调用数据 provider。退出 smoke 前后系统中既有 `codex` 进程数均为 3，本预览进程归零。
- 首次真实成功读取、50 次成功读取、首次读取后工作集和真实数据稳定性在可提供 `0.144.5`（或修复该能力的后续稳定 CLI）前仍是明确未完成的环境门禁。
