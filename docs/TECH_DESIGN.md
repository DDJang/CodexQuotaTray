# CodexQuotaTray 技术设计

文档状态：只读 MVP、Win32 host 与本地打包已实现；公开发布 gate 尚未全部满足
协议基线：`codex-cli 0.144.5` stable App Server schema
最后更新：2026-07-18

## 1. 事实、要求与设计决定

本文使用以下标签，避免把未来方案误写成 App Server 保证：

- **已确认**：由 0.137.0 生成 schema、P0 脱敏实跑或已通过测试直接证明。
- **当前实现**：当前代码和测试采用的行为，但不代表服务端协议承诺。
- **拟议设计**：后续里程碑应实现的架构选择，必须经过对应里程碑评审。
- **产品要求**：来自 `PRD.md` 或 `AGENTS.md`，可能受协议能力限制。

## 2. 范围

本设计覆盖：

- Codex App Server 子进程管理。
- stdio JSONL transport 和最小协议面。
- 账户与额度响应解析。
- 稀疏通知合并、应用状态和故障恢复。
- 隐私边界、测试分层和版本兼容。

本设计同时覆盖已实现的 Windows 托盘 host、弹出卡片、系统提醒、最小设置和开机启动边界。任何 reset-credit 展示推断或消费操作仍不在范围内。

### 2.1 WinUI 迁移隔离边界

第三阶段预览版以 `QuotaRuntimeService` 作为唯一运行时所有者。Startup、Manual、CardOpened、Resume、NetworkRestored、Scheduled 和 App Server 推送补读均进入同一 `RefreshCoordinator`；任何 XAML、托盘或 host event 回调都不能直接读取协议。默认持久化目录为 `%LOCALAPPDATA%\CodexQuotaTray-WinUI-Preview`，正式 Rust 目录只可显式只读导入。

App Server 的 `account/rateLimits/updated` 作为稀疏 patch：存在基线时只覆盖非空字段，通知未携带重置卡字段时保留完整读取快照；没有基线且 patch 不能独立规范化时不更新 UI，只请求受协调器约束的补读。ManualOnly 会抑制该补读，但仍允许安全的完整推送更新。

提醒 reducer 只使用可靠的原始 remaining percentage。窗口稳定 ID 持久化为带 `sha256:` 前缀的本地伪匿名标识，原始 limit ID 不进入文件；通知按“更新状态、原子保存、请求 Windows 通知”执行，语义为 at-most-once 而非保证可见。

`winui/` 是第二阶段并行预览，不是正式入口。Core 已接入只读 stdio JSONL App Server、ID 路由、超时/取消、DTO、规范化和展示投影；App 默认读取一次真实额度，`--demo` 才使用静态数据。预览拥有独立进程名、单实例键和托盘 GUID，不访问正式用户目录，也不接入自动刷新、提醒、缓存、启动项或安装器。完整模块映射和替换门禁见 `docs/WINUI_MIGRATION.md`。

## 3. P0 已确认的协议行为

### 3.1 Transport 与握手

- **已确认** App Server 默认接受 `stdio://`；stdout 每行一个 JSON 消息。
- **已确认** 线上消息省略标准 JSON-RPC 的 `jsonrpc: "2.0"` 字段。
- **已确认** 客户端必须首先发送 `initialize`，成功响应后发送 `initialized`。
- **已确认** 0.137.0 的 `initialized` schema 只要求 `method`，P0 发送 `{ "method": "initialized" }` 并成功。
- **已确认** 初始化响应包含 `userAgent`、`codexHome`、`platformFamily`、`platformOs`；应用只使用前三个非路径展示字段中的 `userAgent` 和平台字段，不传播 `codexHome`。

### 3.2 账户与额度读取

- **已确认** `account/read` 要求对象参数；P0 明确发送 `refreshToken: false`。
- **已确认** `account/rateLimits/read` 无业务参数；P0 发送 `params: null`。
- **已确认** ChatGPT account response 在 wire 上包含 email，但业务摘要不需要该字段。
- **已确认** `rateLimits` 是向后兼容的单 bucket 视图；`rateLimitsByLimitId` 是可空的多 bucket 视图。
- **已确认** P0 实跑同时收到上述两个视图，并在 `primary` 收到一个 10080 分钟窗口，`secondary` 为 null。
- **已确认** `RateLimitWindow.usedPercent` 在 schema 中必填；`windowDurationMins` 与 `resetsAt` 可空，时间戳单位为 Unix 秒。
- **已确认** `account/rateLimits/updated` 是稀疏更新。schema 明确要求客户端合并到最近快照或重新读取，且空缺元数据不能清除已知值。
- **已确认** 10 秒实跑期间未实际观察到更新通知；通知解析和合并仅由生成 schema 与 fixture 测试验证。

### 3.3 Reset credit 限制

- **已确认** 0.144.5 stable schema 在 `account/rateLimits/read` 顶层提供可空的 `rateLimitResetCredits`；其中 `availableCount` 是权威数量，`credits` 可空且可能是截断明细。
- **客户端设计** 协议兼容性采用能力检测：初始化和实际读取成功即启用功能，不用 CLI 版本字符串做相等性门禁。稀疏 updated 通知不会清空完整读取保存的重置卡快照。
- **隐私边界** 重置卡 opaque ID、标题、描述和原始响应不进入 UI、日志、诊断或持久化；UI 仅投影权威数量、明细条数和可解析的最早到期时间。
- **已确认** 当前 `credits { hasCredits, unlimited, balance }` 是通用 credits snapshot，不能解释为 reset-credit 次数。
- **已确认** 当前生成 schema 没有 reset-credit 消费方法。
- **设计结论** 在 schema 提供明确只读数量前，产品状态必须是“重置次数不可用”，不能显示 0，也不能根据 credits 推断。

## 4. 逻辑架构

```text
CodexProcessSupervisor
        │ child stdin/stdout/stderr
        ▼
JsonlTransport ──► JsonRpcClient ──► ProtocolDecoder ──► QuotaProjector
                                            │
                                            ▼
                                      AppStateReducer
                                            │
                         ┌──────────────────┴──────────────────┐
                         ▼                                     ▼
                 NonSensitiveCache                    Win32 UI/Notifications
```

### 4.1 组件职责

- **当前实现 — `app_server`**：发现 Codex、启动子进程、读写 JSONL framing、排空 stderr、关闭和回收进程；不负责响应路由。
- **当前实现 — `supervisor`**：后台轮询子进程退出状态，发布连接代次，处理显式 transport 恢复请求，并执行有界 restart/backoff。
- **当前实现 — `json_rpc`**：生成连接内唯一 ID，维护多个 pending request，按 ID 分派乱序响应，区分成功、错误、通知和脱敏诊断，并统一处理请求超时与 stdout EOF。
- **当前实现 — `protocol`**：只定义握手、账户读取、额度读取及更新通知所需 wire types。
- **当前实现 — `quota`**：优先读取多 bucket 视图，将窗口归一化为与 `primary`/`secondary` 语义无关的 domain model。
- **当前实现 — CLI probe**：通过 `json_rpc` 发起有限时只读探测、消费通知，并负责人类可读输出和退出码；生产托盘使用 `runtime`。
- **当前实现 — `state`**：纯 `AppStateReducer` 是唯一状态转换入口，线程安全内存 store 返回 owned snapshot；UI 不得直接消费 wire JSON。
- **当前实现 — `runtime`**：把 supervisor 连接代次、握手、并发只读 RPC、refresh coordinator、稀疏通知和 reducer 串为一个长期后台 worker；公开接口只暴露 normalized snapshot、刷新触发和幂等 shutdown report。
- **当前实现 — `persistence`**：settings 与 quota cache 分文件；cache 只保存匿名窗口数字和版本 provenance，恢复为 stale，I/O/corruption 只产生匿名 warning。
- **当前实现 — `ui_model`**：把 normalized `AppState` 投影为 tray icon severity、tooltip、card rows、reset-unavailable 和可操作状态文案；不引用 wire types。
- **当前实现 — `alerts`**：阈值 reducer 按窗口/周期追踪 50%、20% 和 10%，通过带 schema version 的本地状态跨重启去重；首次观察与新启用阈值只建立基线。
- **当前实现 — `windows_tray`**：使用 Win32 Shell/WindowsAndMessaging/GDI/Registry adapter 呈现 `ui_model`、触发只读 runtime refresh、发出系统提醒并管理最小设置；不接触 wire JSON、token 或账户标识。

## 5. 进程生命周期

### 5.1 状态机

当前 supervisor 与 runtime 共同执行以下生命周期；每次连接代次都重新握手，pending request 不跨代次重放：

```text
Stopped → Starting → Handshaking → Ready → Stopping → Stopped
              │            │          │
              └────────────┴──────────┴──► Failed → Backoff → Starting
```

### 5.2 启动

1. **当前实现** Windows 依次尝试 `codex.cmd`、`codex.exe`、`codex`；显式 `--codex-bin` 覆盖发现逻辑。
2. **当前实现** 握手后只从该 App Server 的 `userAgent` 提取版本 token，并与 `schemas/CODEX_VERSION` 比较；避免另启 `codex --version` 可能探测到不同安装。match、mismatch 与 unreported 都进入 normalized state，完整 user-agent 不保留。
3. 启动 `codex app-server --stdio`，三个标准流全部 pipe。
4. 在 10 秒内完成 `initialize`。
5. 发送 `initialized`，随后发出两个只读读取请求。
6. 在 15 秒内收到两个可解析响应后进入 `Ready`。

版本不匹配不是自动可恢复错误：继续运行只能作为 best-effort，并必须展示 schema mismatch 状态；发布版默认应拒绝使用未验证的破坏性协议变化。

### 5.3 正常运行与退出

- **当前实现** CLI probe 在首次快照完成后按参数限时监听通知；生产托盘由长期 `runtime` 维护连接。
- **当前实现** supervisor 同一时间只维持一个 App Server 子进程；重启后发布新连接代次，旧 JSON-RPC pending 不跨进程重放。
- **已确认** P0 关闭 stdin 后子进程在三秒内自然退出，退出码为 0。
- **当前实现** 三秒后仍未退出才 kill 并 wait；强制终止被视为清理失败。
- **当前实现** `AppServer` 与 supervisor shutdown 均幂等；重复调用返回第一次的相同脱敏报告。
- **当前实现** Windows `WM_QUERYENDSESSION`、`WM_ENDSESSION`、窗口退出和 `--shutdown-existing` 均接入同一幂等 shutdown path；控制命令通过 `WM_CLOSE` 请求 UI 线程正常回收 runtime 与 App Server，并等待目标进程退出后才返回。App Server 子进程位于 kill-on-close Job Object 中，确保 npm 命令 shim 退出后仍持有 stdio 的后代也能被回收。

## 6. 故障恢复

### 6.0 P1 JSON-RPC 通信可靠性

- **当前实现** 每个连接使用从 0 开始的单调递增 `i64` 请求 ID；达到整数上限时拒绝新请求，不回绕复用。
- **当前实现** pending map 在写入请求前登记，因此快速响应不会丢失；多个请求可同时 pending，完成顺序不影响投递。
- **当前实现** 每个请求保存独立 deadline；超时会删除 pending entry，迟到响应只产生匿名诊断。
- **当前实现** stdout EOF、读失败或 stdin 写失败会关闭 transport，并用同一受控错误结束所有 pending 请求。
- **当前实现** 已完成 ID 保留有限历史，用于识别重复响应；未知 ID、非整数 ID、非法 JSON 和非法 envelope 不会 panic，也不会被错误投递给其他请求。
- **当前实现** RPC error 只向上暴露请求 ID 和 error code；服务端 message 不进入错误对象或日志。
- **当前实现** App Server 重启和 supervisor backoff 已接入 runtime；旧连接的 pending request 会失败，新代次只从新的 startup/network-restored refresh 重新读取，不盲目重放旧 wire request。
- **当前实现** schema mismatch 不会触发重启循环；runtime 产生匿名兼容性 warning，并继续 best-effort 执行固定只读 allowlist。解析失败仍按 protocol failure 处理。

### 6.1 错误分类

| 类别 | 示例 | 是否自动重试 | 状态处理 |
|---|---|---:|---|
| 启动错误 | CLI 不存在、权限拒绝 | 有界 | 无缓存则 unavailable；有缓存则 stale |
| 握手/协议错误 | timeout、缺少 result、schema mismatch | 有界；版本不匹配不循环重试 | 保留旧状态并记录 protocol failure |
| 认证状态 | account 为 null、API Key、Bedrock | 否，等待用户操作 | unauthenticated 或 unavailable |
| 读取失败 | RPC error、网络服务不可达 | 有界 | 保留最后有效快照 |
| 子进程意外退出 | EOF、非零退出码 | 有界重启 | offline/backoff |
| 数据不完整 | 缺少 usedPercent | 不把字段补成 0 | 跳过不完整窗口并产生 warning |

### 6.2 重试策略

以下 restart 与 read-only refresh 路径均已实现：

- **当前实现** 单个连接代次同一时间只允许一个初始化流程；恢复生成新代次。
- **当前实现** 重启 backoff 为 1、2、4、8、16、30 秒上限，并加入不超过总上限的 0–20% deterministic jitter。
- **当前实现** 五分钟窗口内最多自动重启 5 次；超过后发布 exhausted 事件并停止重启。
- **当前实现** stderr 始终由独立线程排空，仅聚合 `stderr_observed`，不保存或传播原始文本。
- **当前实现** 非零退出、stdin 写失败、stdout EOF 和显式 transport 恢复均可进入同一 bounded restart path；shutdown 可中断 backoff。
- 读取请求失败使用同样上限，但不得重启仍然健康的 App Server，除非 transport 已关闭。
- 自动、通知和系统事件触发的主动读取最小间隔为 10 秒；显式手动刷新在没有 in-flight 时立即执行，并发刷新仍合并为一个 in-flight 请求。
- 认证模式不支持和 schema version mismatch 不进行无意义的快速重试。

### 6.3 Refresh coordinator

- **当前实现** 同一时间最多一个 quota refresh；并发触发合并为一个最高优先级 pending reason。
- **当前实现** 非手动主动刷新最小间隔为 10 秒，请求 deadline 为 15 秒；显式手动刷新跳过空闲节流但不绕过单 in-flight。UI 在首次启动同步、运行时 refreshing 或短暂手动反馈期间启用临时 100ms 状态轮询，完成后立即停止；因此刷新完成不依赖重新打开卡片或 30 秒空闲计时器。Auto、固定 5/15/30 分钟与 ManualOnly 由统一策略生成调度。
- **当前实现** startup、manual、rate-limit notification、resume、network restored、card opened 和 scheduled 使用同一调度路径。
- **当前实现** 未知或重复 completion 不修改当前 in-flight；request ID 单调且不回绕。
- **已确认** 纯虚拟时间测试覆盖五种模式、唯一请求 ID、单 in-flight、合并、超时、失败退避、成功复位和模式热更新。
- **当前实现** runtime executor 每次逻辑 refresh 同时发出 `account/read` 与 `account/rateLimits/read`，按请求 ID 等待各自响应；通知先执行安全稀疏合并，再经最小间隔调度一次权威完整补读。
- **当前实现** card-open 只在没有成功数据或数据至少 60 秒未更新时映射到 `CardOpened`；`PBT_APMRESUMEAUTOMATIC` 和 Windows Network Connectivity Hint 的 InternetAccess 分别映射到 `Resume` 与 `NetworkRestored`。映射本身是纯函数，最终仍受同一 coordinator 限流。

### 6.4 Windows host 边界

- **当前实现** Windows 依赖只在 `cfg(windows)` 下启用；生产依赖是 Microsoft `windows` 0.62.2 的所需 Win32 namespace features，不包含 WebView、Electron 或 Chromium。网络 monitor 只使用系统 IP Helper connectivity hint，不主动探测网站。
- **当前实现（0.1.4）** UI 保留 Win32/GDI，不引入 WinUI 3 或 Windows App SDK。卡片不使用 `WS_EX_LAYERED` 或整窗 Alpha，始终在不透明 `#F5F7FA` 表面以 ClearType Natural 绘制；Windows 11 请求浅色 DWM 模式、系统圆角和阴影，失败不影响业务状态。
- **当前实现（0.1.4）** `windows_visuals` 以逻辑像素生成 0–3 个额度窗口的统一布局，96/120/144/192 DPI 均由同一几何数据驱动绘制和 hit-test。状态徽章区域已移除，状态作为标题下方的语义颜色文本；GUI 入口显式验证 PerMonitorV2，资源脚本以标准 `RT_MANIFEST` 数值类型 24 嵌入清单；卡片显示前读取光标所在显示器的有效 DPI，并使用 `rcWork.right - width` / `rcWork.bottom - height` 定位到任务栏上方，继续处理 `WM_DPICHANGED`。
- **当前实现（0.1.4）** 用户提供的 24-bit PNG 保留为源资源；构建脚本生成抗锯齿、透明圆角的 32-bit preview 以及 16–256 px ICO，并验证 Alpha 与九个 frame。构建期 `embed-resource` 只调用 Windows resource compiler，将 icon 与 manifest 链接到 EXE；它不进入运行时。应用在注册窗口类前从当前 HINSTANCE 同步加载 32px、16px 和独立托盘 HICON；正常、离线和刷新初始状态均使用产品托盘图标，不使用 `IDI_QUESTION`、`IDI_INFORMATION` 或 `IDI_APPLICATION` 占位回退。`scripts/verify-pe-icon.ps1` 会从最终 PE 的 `RT_GROUP_ICON #101` 读取并验证 16/20/24/32/48/256 等尺寸及其 `RT_ICON` 子资源。
- **当前实现（DPI 图标清晰度）** 注册窗口类的启动图标与当前 DPI 的窗口大/小图标、托盘图标分离管理。窗口和托盘尺寸通过 `GetSystemMetricsForDpi` 选择不小于目标物理尺寸的内嵌 ICO frame；弹出卡片标题区采用纯文本布局，不再绘制易受 Shell HICON 栅格化影响的装饰图标。`WM_DPICHANGED` 先加载完整的新窗口/托盘图标集合，替换成功后才释放旧动态句柄；加载失败保留旧产品图标并记录诊断日志，不显示系统占位图标。
- **当前实现** UI 每 30 秒只重新投影内存 snapshot 以更新时间文案；它不直接发网络请求。所有主动读取都经可配置的 refresh coordinator 控制。
- **当前实现** 30 秒 timer 仅在投影内容实际变化时 invalidates/repaints；状态/通知由事件即时投递，正常倒计时最多每分钟改变一次，避免无变化的 GDI 重绘占用空闲 CPU。
- **当前实现** JSON-RPC dispatcher 的空闲阻塞检查保持 25 ms，supervisor/runtime 为 250 ms；请求仍使用独立 15 秒 deadline，stdio 到达会立即唤醒 dispatcher。dispatcher 不放宽，因为当前收发互斥边界下会延迟并发写入并破坏可靠性测试。
- **当前实现** 单实例按固定 window class 发现；再次启动只激活已有实例。安装器/卸载器可使用 `--shutdown-existing` 请求正常退出。
- **当前实现** connectivity callback 只把 HWND 值作为 callback context，并向 UI 线程投递自定义消息；不跨线程借用 Rust 状态。注销失败不会跳过 runtime shutdown，后续调度仍由当前刷新模式决定。
- **当前实现** quota balloon 加入 `NIIF_RESPECT_QUIET_TIME | NIIF_NOSOUND`，让 Windows 安静时段/专注设置决定展示，并避免应用自行播放声音。
- **当前实现** 卡片使用 normalized tooltip 作为动态窗口标题，提供 `Enter` 刷新、`Tab`/方向键焦点移动、`Space` 执行当前操作、`F10` 菜单和 `Esc` 关闭；标准 Win32 popup menu 是主要键盘/辅助技术命令面。
- **当前实现（0.1.4）** 托盘使用独立的 `HWND_MESSAGE` 消息窗口作为 `NOTIFYICONDATAW.hWnd`。`NOTIFYICON_VERSION_4` 回调只取 `LOWORD(lParam)`，仅 `WM_LBUTTONUP` 投递一次 `WM_APP_TOGGLE_WINDOW`；右键通过独立消息投递菜单。主 UI 线程以 `desired_visible` 作为唯一显隐状态源，显示时再次单击隐藏、隐藏时单击显示。刷新、绘制、布局、DPI 和 `WM_ACTIVATE/WM_ACTIVATEAPP` 只记录或重绘，不自动隐藏，也没有失焦定时器或点击防抖。Explorer 仅在 `TaskbarCreated` 后重新执行 `NIM_DELETE → NIM_ADD → NIM_SETVERSION`，不改变显隐状态。
- **当前实现（UI 紧凑化）** 卡片按额度窗口数量和设置警告动态计算高度，移除独立的“最后更新”区域；标题下状态行合并为更新时间或失败重试提示，额度窗口将倒计时和本地重置日期分成两行。重置次数不可用仅作为低权重信息条显示，不改变额度数据或协议边界。
- **当前实现（UI 文案与颜色）** 套餐名称 `plus` 仅在展示层规范化为 `Plus`；按钮正文使用“刷新”和“打开官方用量页面”，不在 UI 或键盘处理上暴露 R/U 快捷键。百分比和进度强调色按剩余值分为绿色（>50%）、橙色（20%–50%）和红色（<20%），不影响托盘状态阈值。
- **已评审限制** quota rows 是自绘 GDI 内容，不分别暴露 UI Automation 节点；屏幕阅读器只能读取聚合窗口标题与标准菜单。P4 发布前必须决定是否接受该 MVP 边界，或改用原生 child controls/UIA provider。

### 6.5 Packaging 与发布边界

- **当前实现** P4 产物是面向当前用户的 Windows x64 ZIP，不要求管理员权限；安装目录固定为 `%LOCALAPPDATA%\Programs\CodexQuotaTray`，设置与可选额度缓存保持在独立的 `%LOCALAPPDATA%\CodexQuotaTray`。
- **当前实现** package script 使用 `Cargo.lock` 和 locked `cargo rustc` release 编译，目标依赖图固定为 `x86_64-pc-windows-msvc`，并以 `--remap-path-prefix` 从最终 crate 移除本地 repoRoot。归档内容采用显式文件白名单，包含原生 EXE、安装/卸载脚本、SHA-256 manifest、项目许可证、隐私说明、依赖清单和从本地 Cargo metadata 收集的完整第三方许可证文本。
- **当前实现** installer 在复制前验证 manifest 的完整文件集合和每个 SHA-256；拒绝 reparse-point 安装目录，先通过 `--shutdown-existing` 请求旧进程正常退出，再以 temporary-file move 和有限重试替换文件。uninstaller 只允许删除两个预期的精确子目录，默认删除设置/cache，并提供显式 `-KeepUserData`。
- **当前实现** 启动项只写当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，且值为完整、加引号的 EXE 路径；隔离 smoke 使用一次性 HKCU 测试 key，不修改真实启动项。
- **当前实现** 托盘启动时只把与当前 EXE 精确匹配的 Run 值显示为已启用，避免 installer/setting JSON 状态分叉或旧路径被误报为当前启动项。
- **当前实现** CLI 启动失败、有限 backoff、schema version mismatch 和 unreported version 都投影成可操作、无身份信息的 UI 文案；quota 缺失时不伪造 0% 或 100%。
- **已确认** release binary 是单个原生 Win32 进程，不打包 WebView、Electron 或 Chromium；当前 version-matched schema 没有 reset-credit count/consume contract，package 也没有相关入口。
- **发布决策** 当前本地归档是 unsigned developer build。公开发布需要组织控制的 Authenticode/Trusted Signing 身份：先在受控环境构建和签名 EXE/脚本，验证签名后重新生成 manifest/ZIP，并发布外部 checksum 与 provenance。仓库不持有签名凭据，也不自动发布。
- **开放门禁** Windows 11 当前环境已有安装与 tray smoke；Windows 10、稳定渠道 Windows 11、多 DPI/多显示器、签名验证和 PRD 七天 soak 仍是公开发布前门禁，不得从现有自动化结果推断为已完成。

## 7. 状态管理

### 7.1 当前状态模型

```text
ProcessState = stopped | starting | handshaking | ready | backoff | failed
AuthState    = authenticated | unauthenticated | api_key | bedrock | unknown
DataState    = empty | refreshing | fresh | stale | offline | unavailable

QuotaState:
  account_mode
  plan_type?
  buckets[]
  rate_limit_reached
  last_success_at?
  last_attempt_at?
  source_cli_version
  reset_credit_state = unavailable_in_schema
  non_sensitive_warning_codes[]
```

### 7.2 更新规则

- **当前实现** 非空 `rateLimitsByLimitId` 优先；否则回退到 `rateLimits`，避免重复展示 legacy bucket。
- `limitId` 用于 bucket identity，`limitName` 用于展示；两者缺失时生成本地匿名 key，不持久化服务端未知 ID。
- `primary` 和 `secondary` 只保留为 source slot，不决定显示名称。
- `windowDurationMins` 决定 5-hour、7-day 或动态时长名称。
- `remainingPercent = clamp(100 - usedPercent, 0, 100)`；越界输入产生 warning。
- `rateLimitReachedType` 属于 bucket snapshot 而不是单个 window；parser 聚合为 `rate_limit_reached`，UI/提醒按“服务端已报告限制”处理，但不把它猜测分配给 `primary` 或 `secondary`。
- 稀疏通知只覆盖 `Some` 字段；null/缺失字段不清除已有元数据。
- 读取或更新失败不会清空最后有效快照。
- **产品要求** 成功数据超过 15 分钟未刷新后进入 stale；这是产品策略，不是 App Server 协议事实。

所有已实现状态变化均通过纯 reducer 完成；事件携带显式时间，reducer 不读取系统时钟或执行 I/O，因此可使用 fixture 完整重放。

### 7.3 当前 reducer 失败语义

- 刷新开始只把 data state 设为 `refreshing(previous)`，不会删除 quota snapshot。
- transport failure 进入 `offline`；其他读取失败按最后成功时间保持 `fresh` 或进入 `stale`。
- 成功数据满 15 分钟后由显式 `Tick` 转为 stale；缺失字段不会生成 0 或 100。
- 不完整 read、无基线 patch 和多 bucket 歧义 patch 均保留最后有效快照并产生匿名 warning code。
- 明确切换到未登录、API Key、Bedrock 或不支持账户模式时清除旧 ChatGPT quota，避免跨账户模式展示旧数据。
- sparse patch 在私有 typed snapshot 上合并；公开 store snapshot 只包含 normalized domain state。
- cache restore 不恢复 authenticated 状态或官方 limit metadata；只提供 stale 数字快照，实时账户读取可以清除或替换它。

## 8. 隐私与信任边界

### 8.1 允许的数据流

- Codex CLI 自己读取和管理官方认证材料。
- 本应用只通过 child stdio 接收响应；不读取 `~/.codex` token、浏览器 cookie 或网页 DOM。
- 原始 JSONL 只在内存中短暂存在，解析后立即丢弃。
- domain state 仅保留账户模式、套餐类型、额度百分比、窗口时长、重置时间和非敏感状态码。

### 8.2 禁止的数据流

- 不记录 email、access token、refresh token、cookie、完整 account ID、完整 reset-credit ID、`codexHome` 或原始认证响应。
- stderr 必须持续排空以防死锁，但默认不显示、不缓存。
- JSON-RPC error message 默认不原样打印；只输出 error code 和本地维护的可操作说明。
- fixture 只能包含 `[REDACTED]` 或人工构造值，不能复制真实响应正文。
- 非敏感缓存不得成为认证缓存。
- cache 白名单仅包含 used percent、duration、reset time、last-success time、source CLI version 和 source slot；limit ID/name、plan/auth mode 均不落盘。

## 9. 测试策略

### 9.1 已有覆盖

- **已确认** parser、JSON-RPC、supervisor、state reducer、refresh coordinator、version compatibility、persistence、runtime、UI projection、alert 和 host-event 测试完全离线运行；准确数量以当前 `cargo test --all-targets` 输出为准。
- 覆盖 ChatGPT、API Key、Bedrock、未登录、single/dual/multi bucket、未知时长、缺失字段、越界百分比、malformed JSON 和稀疏合并。
- JSON-RPC 测试覆盖唯一 ID、多个 pending、乱序响应、RPC error、通知、timeout、EOF、未知/重复 ID、null result、非法 JSON 和非法 envelope。
- supervisor 测试覆盖非零退出恢复、restart budget、启动失败、显式恢复、stderr flood、正常 EOF、强制回收、幂等 shutdown，以及命令 shim 退出后仍持有 transport pipe 的后代进程树回收。
- reducer 测试覆盖 process/auth/data 状态、失败保留、15 分钟 stale、完整替换、稀疏更新、歧义拒绝和 owned store snapshot。
- runtime 测试通过真实本地 stdio pipe 覆盖乱序并发响应、启动快照、手动刷新合并、通知稀疏合并与完整补读、单代次崩溃恢复和幂等关闭。
- version 测试覆盖生成记录读取、精确 match、pre-release mismatch 与 unreported；mismatch runtime 测试同时证明 quota 仍可 best-effort 投影。
- persistence 测试覆盖设置默认/往返/越界、字段隐私白名单、完整替换、backup recovery、损坏/超大输入和幂等清除；runtime 测试覆盖 stale cache → live replacement/write-back 与损坏缓存非阻断恢复。
- UI projection 测试覆盖 duration 命名、剩余/已用模式、阈值 icon、refreshing 保留、stale/offline 降级、未登录/API Key/Bedrock 和缺失字段；alert 测试覆盖首次基线、20/5/0 去重、跨周期恢复、禁用策略与多窗口独立性。
- 请求序列测试证明 runtime 只使用四个允许 method，通信层统一注入 request ID。
- 脱敏实跑证明真实 quota 可读、默认发现 `codex.cmd` 可用且 stdin close 能干净退出。
- 90 秒真实 runtime soak 保持 generation 0 / fresh / 单窗口，完成 1 次读取、0 failure、0 restart、0 forced termination、0 protocol diagnostic；退出后进程查询无遗留 App Server。
- Windows 11 10.0.26200 x64 手工 smoke 验证 demo 卡片、中文动态窗口、按钮刷新、右键菜单勾选和菜单退出；真实 release host 能启动 P1 runtime，并通过正常控制路径退出且不遗留额外 App Server。
- P3 release GUI 二进制为 932,352 bytes；真实 host 稳定后 10 秒空闲窗口 working set 从 10,145,792 降至 10,096,640 bytes，CPU 增量 0 秒、6 threads、139 handles。App Server/Node 子树资源由独立 P1 soak 单独记录。

### 9.2 后续测试层级

- **Unit**：wire → domain parsing、reducer transitions、backoff 计算、fresh/stale 判定。
- **Fixture contract**：每次升级 Codex CLI 后重新生成 schema，并为新增/变化字段添加匿名 fixture。
- **Fake process integration**：使用本地假 App Server 测试乱序响应、EOF、timeout、stderr flood、非零退出和强制清理；不得连接真实账户。
- **Optional live smoke**：仅开发者显式运行，只允许读取方法；结果只记录成功/错误类别，不保存真实额度。
- **Privacy regression**：扫描日志、cache、fixtures 和快照，确保不存在 email 或 token-like 内容。
- **Soak/performance**：后续常驻服务至少运行 24 小时，验证无进程泄漏、无高频轮询和内存持续增长。

## 10. 已知风险与开放门禁

- reset-credit count 在当前版本不可实现；在新 schema 出现前阻塞相关产品承诺。
- 更新通知尚未在 live run 中观察到；在生产依赖事件刷新前必须延长 smoke/soak 验证。
- App Server schema 与 CLI 版本绑定；升级必须经过 schema diff、fixture 更新和兼容测试。
- `rateLimitsByLimitId` 可能包含多个未知 bucket；UI 设计前必须先验证 domain ordering 和命名策略。
- 当前只在 Windows 11 10.0.26200 x64 做过手工托盘 smoke；Windows 10 与不同 DPI/多显示器组合仍需发布前验证。
- 自绘 quota rows 没有逐项 UIA 节点；当前以动态聚合标题、键盘快捷键和标准系统菜单提供最低可访问路径。

## 11. 0.2.0 提醒与刷新架构

- `RefreshCoordinator` 是 Startup、Manual、CardOpened、Resume、NetworkRestored、RateLimitNotification 和 Scheduled 的唯一入口，维持单 in-flight，并以单调时钟安排自动区间、固定间隔和 1/2/5/15 分钟退避。
- `ManualOnly` 只允许显式 Manual 主动读取；transport 仍初始化并监听推送。无可靠基线的稀疏推送不会被猜测为完整快照。
- stale 门槛由纯函数计算：普通模式为 `max(15 分钟, 有效刷新间隔 × 2)`，ManualOnly 为 60 分钟；认证/兼容错误、刷新中和最近失败优先于 stale。
- 提醒 reducer 只使用协议层验证后的原始 remaining percentage，跨越定义为 `previous > threshold && current <= threshold`。首次状态和新启用阈值只建基线，不补发历史通知。
- `alert-state.json` 先于 Windows 通知原子写入，形成 at-most-once、优先防重复语义。通知系统失败允许漏发；产品不保证通知一定可见。
- 周期比较使用 UTC。reset 时间需向后推进 `max(5 分钟, 窗口时长一半)`；仅缺少可靠时间时才使用“上升至少 50 点且达到 80%”的备用规则。
- 稳定 `limit_id` 仅在内存中计算 SHA-256，磁盘保存的是本地伪匿名标识；原始 ID、账户信息和 limit name 均不持久化。
