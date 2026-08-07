# CodexQuotaTray Android Roadmap

这是 CodexQuotaTray 的个人使用、实验性 Android 路线。最终目标是一个独立的
Android APK：安装后可以在 App 内完成 Codex 登录并查看额度，不依赖 Windows、
Termux、Python、Node/npm、云服务器或其他外部 App。

本路线不承诺应用商店发布、隐私合规、多用户支持或完整 Codex 功能。当前生产
Windows + WinUI 入口继续保持不变，Android 实验代码不得修改 `winui/`。

## 主路线与兼容边界

主路线调整为：

```text
P0  Android ARM64 Codex runtime feasibility
 │
 ▼
P0.5  Standalone APK / embedded runtime
 │
 ▼
  P1  Authentication + real quota integration
 │
 ▼
P2  Product quota UI
 │
 ▼
  P3  Minimal polish / recovery / packaging
```

- Termux Bridge 从主路线移除。现有 `android/bridge/` 只作为开发验证和诊断备用，
  不作为 APK 运行时依赖。
- Python P0 探针继续保留，用于回归和真实设备诊断。
- 当前首选方案是：APK 以 `arm64-v8a` native library 形式内置 Android ARM64
  Codex runtime，App 通过 `applicationInfo.nativeLibraryDir` 找到并启动它，Kotlin
  直接与 App Server 通信。
- 不把 Rust JNI、NDK 或 Codex crates 嵌入作为当前方案；不 fork 或照搬完整
  Codex Mobile。
- App Server 仍是唯一 Codex 协议对端。Android App 不读取 Termux 凭据，不依赖
  Termux 的 PATH、HOME、Node/npm 或 Python。

## 阶段状态

| 阶段 | 目标 | 前置条件 | 当前状态 |
| --- | --- | --- | --- |
| P0 | Android ARM64 Codex runtime feasibility | 无 | Go；真实 ARM64/Termux smoke 已完成 |
| P0.5 | Standalone APK / embedded runtime | P0 Go | Go；真实 ARM64 APK smoke 已完成 |
| P1 | Authentication + real quota integration | P0.5 Go | Go；登录、真实额度和认证持久化已完成 |
| P2 | Product quota UI | P1 Go | Go；真实主页面、手动刷新和重启复验已完成 |
| P3 | Minimal polish / recovery / packaging | P2 Go | Next |

P0 和 P0.5 都是硬门禁；对应门禁未通过时，后续阶段保持 `blocked`。P0、P0.5 和
P1 的真实 Android ARM64 验收均已通过，因此 P2 已通过，P3 为 `Next`。能构建 APK 或在桌面
运行 JVM 测试，不能替代真实 Android ARM64 安装验收。

## 共享额度语义

已验证或后续 Android 层必须保持以下语义：

- 动态识别额度窗口，不把 `primary`、`secondary` 固定解释为 5 小时或 7 天。
- `rateLimitsByLimitId` 非空时优先于 legacy `rateLimits`。
- 缺失、`null` 或 malformed 不等于零；零窗口是合法且可表达的状态。
- `usedPercent` 越界时可以 clamp，但必须保留百分比不可靠标记。
- `account/rateLimits/read` 成功与窗口数量独立判断。
- 失败不能把缺失窗口当作零；本轮 UI 只显示当前读取结果，不实现缓存恢复。
- reset-credit 完整五态、稀疏通知、缓存和后台刷新不属于 P2。

## P0：Android ARM64 runtime / App Server 可行性

P0 已在真实 Android ARM64 + Termux 上完成：社区版 Codex runtime 可以启动，
`initialize`、`account/read`、`account/rateLimits/read` 连续三次成功，认证持久化、
额度读取、进程回收和来源指纹均已记录。

Termux 只作为 P0 验证环境，不是最终产品安装前提。P0 探针保留在
`android/poc/p0_handshake.py`。

## P0.5：Standalone APK / embedded runtime（Go）

P0.5 的唯一目标是证明下面的链路在真实 Android ARM64 手机上成立，且不安装或启动
Termux：

```text
APK
  -> APK 的 lib/arm64-v8a/libcodex_exec.so
  -> applicationInfo.nativeLibraryDir/libcodex_exec.so
  -> codex --version
  -> codex app-server --listen ws://127.0.0.1:<port>
  -> initialize
  -> account/read
  -> account/rateLimits/read
```

### Runtime 输入

构建时允许通过以下任一方式指定 runtime 目录或 zip：

- 环境变量 `CODEX_ANDROID_RUNTIME`；
- Gradle property `codexAndroid.runtime`。

构建脚本必须明确输入是否存在、输入类型和最终打包内容。不能把 npm launcher
或 `codex.js` 当成完整 runtime；需要确认实际 native executable 及其伴随动态库，
例如 `libc++_shared.so`，并在构建报告中列出文件。构建阶段将 `codex.bin`/`codex`
原样复制为 `arm64-v8a/libcodex_exec.so`，由 Android 安装到 native library 目录；
不把 ELF 解压到可写的 `filesDir` 后执行。

本轮已核对参考项目和 runtime 入口：`@mmmbuto/codex-cli-termux` 的 package
manifest 明确区分 `bin/codex.bin`、`bin/libc++_shared.so`、shell launcher 和
`codex.js`；DioNanos 仓库的 `v0.146.0` Android ARM64 release asset 已下载并以
其 `SHA256SUMS` 校验，SHA-256 为
`fcb7b2315443c7145f30be67ff099c965364be85b0daf8a20237042172f18533`。
实际 archive 包含 `package/bin/codex.bin`、`package/bin/libc++_shared.so` 及
launcher 文件；APK 只接受并检查 native ELF，不接受单独的 npm/Node launcher。
SHA-256 校验不等于已证明该 ELF 能从普通 Android App 私有目录执行；后者仍属于
P0.5 真机门禁。

### App 私有目录与进程

运行时启动时：

- 使用 `context.applicationInfo.nativeLibraryDir/libcodex_exec.so`，不复制或执行
  `filesDir` 中的 ELF；
- 创建 `context.filesDir/codex-home/` 和 `context.filesDir/codex-home/.codex/`；
- runtime 启动环境设置 `HOME` 为 `codex-home`；
- 设置 `CODEX_HOME` 为 `codex-home/.codex`；
- 检查 native library 中的 executable 是否存在且可执行；
- App 自己启动和停止 App Server，只绑定 `127.0.0.1`；
- App 退出时尽量关闭 stdin、停止进程并清理子进程；
- 不做后台常驻、WorkManager、通知或自动重连框架。

P0.5 默认使用 `43128`，Gradle property 可以配置端口。P0.5 不需要 HTTP Bridge；
Kotlin 直接连接本地 App Server。上游将 `ws://` transport 标为 experimental/
unsupported，因此本阶段只是把它作为必须在真机验证的技术风险，不把桌面成功或
参考项目代码视为兼容性证明。若 runtime 仍依赖 Termux、Node/npm、Python 或
其他外部路径，必须明确报告并保持 `blocked`，不能偷偷加入兼容层。

### P0.5 最小协议

只实现以下消息：

1. `initialize` request；
2. `initialized` notification；
3. `account/read` request；
4. `account/rateLimits/read` request（独立记录，不作为 P0.5 总成功的硬条件）。

当前不实现：

- `account/rateLimits/updated`；
- sparse merge；
- reset-credit 完整五态；
- cache；
- retry/reconnect framework；
- 账户写操作、购买或 reset-credit 消费。

P0.5 仍需保留动态窗口、缺失值、零窗口和额度读取独立成功语义。原始响应、token、
完整认证内容和 opaque ID 不进入诊断 UI 或日志。

### P0.5 开发诊断

不做正式产品 UI，只做一个最小诊断页：

```text
Codex Android Runtime PoC

Runtime packaged: yes/no
Native library present: yes/no
Runtime ready: yes/no
Native library dir: ...
Codex executable path: ...
Codex version: ...
App Server started: yes/no
Initialize: success/fail
Account: authenticated/unauthenticated
Rate limits: success/fail
Quota window count: N
Last error: ...

[Run Test] [Stop Runtime]
```

如果成本很低，可以额外显示窗口的 `usedPercent`、`remainingPercent`、
`windowDurationMins` 和 `resetsAt`；不花时间做视觉美化。

### P0.5 Go / No-Go

只有真实 Android ARM64 手机安装 APK 后同时满足以下条件，P0.5 才能标记 Go：

- 不安装、不启动 Termux 也能运行；
- APK 包含 `lib/arm64-v8a/libcodex_exec.so` 和 `lib/arm64-v8a/libc++_shared.so`，
  且安装后的 `applicationInfo.nativeLibraryDir` 中两个文件均可见；
- 普通 Android App 进程可以从该 native library 目录启动 `libcodex_exec.so`；
- `codex --version` 成功；
- App 自己启动 `codex app-server`；
- `initialize` 成功；
- `account/read` 返回成功或明确可解释状态；
- `account/rateLimits/read` 的成功、未认证或明确 RPC 不可用状态可以独立表达；
- P0.5 不要求 App 私有认证已存在，也不要求真实额度窗口非空；登录和真实额度属于 P1；
- 没有 Termux、npm、Node、Python 或桌面路径依赖；
- App Server 停止后没有明显遗留进程。

“能构建 APK”本身不等于 P0.5 Go。

## P1：Authentication + real quota integration（已通过）

P1 是 P0.5 之后的最小产品功能阶段，只处理 App 私有 `CODEX_HOME` 的登录和
`account/rateLimits/read`。本 APK 不重新引入 Termux Bridge，也不增加 HTTP 服务；
当前 Kotlin `AppServerClient` 直接连接本地 App Server。除该 App Server 协议边界外，
不增加第二套兼容层。

登录使用 App Server 已确认的 `account/login/start`，参数为
`{"type":"chatgptDeviceCode"}`，必要时 fallback 到 `chatgpt` browser flow。App
展示一次性 device code 和 verification URL，通过 Android 浏览器 Intent 打开 URL；
不使用 WebView，不复制 Termux `auth.json`，不实现自定义 OAuth，也不把 token 写入
日志或明文配置。登录完成后的同一 App Server 会话使用
`account/read {"refreshToken": true}` 确认认证状态；普通启动和普通刷新保持
`{"refreshToken": false}`，不把主动刷新设为默认行为。随后执行
`account/rateLimits/read`，认证持久化到 App 私有 `CODEX_HOME`。

P1 的额度投影使用动态窗口、缺失不等于零、零窗口可表达和窗口读取独立成功语义；
不把 `primary` 或 `secondary` 固定解释为某个时长。真实手机已完成“未认证 → App 内
登录 → authenticated=true → account/read → rateLimits/read”，并在 force-stop、
重新打开后再次读取成功。登录持久化 smoke 作为 P1 验收结果记录，不单独形成长期
阶段名 `P0.6`。

### P1 非敏感真机结果

- Android ARM64 独立 APK 启动 Codex runtime、App Server、`/readyz`、WebSocket 和
  `initialize` 均成功。
- App 内登录后的 `account/read` 为 authenticated，`account/rateLimits/read` 成功，
  本次返回 1 个额度窗口。
- force-stop 后重新打开 App、无需重新登录，仍为 authenticated，额度读取成功，
  进程清理成功，整体结果为 `Success: yes`。
- 认证、token、设备码、邮箱和完整响应均不写入路线文档；P0.5 来源与构建证据见
  [`android/P0_5_RESULT.md`](../android/P0_5_RESULT.md)。

## P2：Product quota UI（Go）

P2 只做一个主页面，把已经验证的真实额度数据投影为日常可读的 UI：账户类型、动态
额度窗口、剩余百分比、进度条、可用的重置时间、更新时间和手动刷新按钮。窗口数可以
为 0、1 或多个；没有可靠值时显示未知或明确错误，不伪造零值。

P2 只保留 `loading`、`unauthenticated`、`loaded`、`error` 四种 UI 状态。普通刷新
顺序为 ready → initialize（当前会话未初始化时）→ `account/read`（refreshToken=false）
→ `account/rateLimits/read`；只有登录完成后的确认读取使用 refreshToken=true。UI 不
解析原始 JSON-RPC，不依赖 Windows 类型或 Termux 状态，不做通知、Widget、后台刷新、
Room/SQLite 或复杂设置。

P2 已在真实 Android ARM64 手机上验证：force-stop/reopen 后认证保持，
`account/rateLimits/read` 成功，动态窗口、剩余百分比、重置时间、更新时间和手动刷新
均已显示/通过。未登录 UI 由单元测试覆盖；本轮没有为了测试清除真实设备认证。

## P3：Minimal polish / recovery / packaging（Next）

P3 只收尾个人使用所需的最小恢复、设置和打包诊断：启动后恢复已持久化认证、显示
可解释的错误状态、保留可配置端口和 runtime 版本信息。P3 不实现完整 Codex 聊天、
会话历史或 Agent 功能。

## Later

以下内容全部排在 P3 之后：

- 后台自动刷新；
- 通知；
- Widget；
- Termux:Boot；
- 开机启动；
- 更复杂缓存；
- 自动 runtime 更新；
- 多账号；
- 面向发布的单 APK 产品化；
- Play Store；
- 完整 Codex 功能、聊天、会话历史和 Agent。

## 非目标与安全边界

- 不 fork 或照搬完整 Codex Mobile；只参考其 runtime 解压、进程和协议边界。
- 不把 Rust JNI、NDK 或 Codex crates 嵌入作为当前方案。
- 不支持多 ABI、x86 模拟器、root 增强或 Termux:Boot。
- 不实现 HTTP 服务、云端 Bridge、远程访问或账户写请求。
- 不修改当前 WinUI 生产实现。
- 不把 token、Cookie、邮箱、账户 ID、原始响应、stdout/stderr 或 opaque ID 写入
  APK 诊断页、日志或持久化文件。

## 尚未验证的长期假设

以下内容不影响已经完成的 P0–P2 Go 判定，但仍需在后续维护或 P3 评估：

- 后续 Codex runtime 版本仍会提供可在独立 APK 中运行的 Android ARM64 native
  executable 及完整依赖；当前已验证对象仅为 DioNanos `v0.146.0`。
- 后续 Android SDK、AGP、Gradle、JDK 和最低 SDK 调整仍能保持当前构建路径；当前
  validated baseline 是 AGP `8.7.3`、Gradle `8.9`、JDK 17、compile/target SDK 35。
- APK 体积、签名、安装限制和 ABI 兼容范围仍符合个人使用目标；当前只验证了真实
  `arm64-v8a` 设备。
- 手机系统在更长时间的前台/后台切换中不会杀掉 App 或 App Server；当前只验证了
  手动刷新和 force-stop/reopen。
- P3 的恢复、错误呈现和最终个人打包方式仍可在不扩大产品范围的前提下完成。
