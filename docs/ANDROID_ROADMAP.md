# CodexQuotaTray Android Roadmap

这是 CodexQuotaTray 的个人使用、实验性 Android 路线。它不替代当前的
Windows + WinUI 3 正式入口，不承诺公开发布、应用商店分发、隐私合规或多用户支持。
本路线只覆盖 Termux、Termux Bridge 和一个读取本机额度的 Android 客户端。

## 范围与兼容层

- Termux 负责运行 Codex CLI 和 Codex App Server。
- Termux Bridge 是唯一的 App Server 兼容层，负责进程生命周期、stdin/stdout
  JSONL、协议请求、额度归一化、稀疏更新合并和本地状态投影。
- Android App 不解析 App Server 原始 JSON，不读取 Termux 凭据，也不直接启动
  App Server；它只读取 Bridge 提供的版本化本地只读接口。
- Bridge 默认监听 `127.0.0.1:43127`。43127 是默认端口，不是固定协议约束，端口
  必须可以通过启动参数或本地配置修改，并始终保持 loopback 绑定。
- P0 中的 `account/read` 是兼容性探测项。当前正式协议合同的 outbound allowlist
  仍需单独维护；P0 不会未经协议评审就把新的 outbound method 静默加入生产 Bridge。

## 共享语义

Android 路线沿用当前协议合同和 Core 实现中的行为语义，而不是复制 WinUI 代码：

- 额度窗口动态识别，不把 `primary`、`secondary` 固定解释为特定周期。
- `rateLimitsByLimitId` 非空时优先于 legacy `rateLimits`。
- 缺失、null 或 malformed 不等于零；零窗口和不可用状态必须能明确表达。
- 越界百分比可以限制到展示范围，但必须保留不可靠标记。
- 稀疏通知只覆盖明确出现的字段；未出现的窗口、bucket、metadata 和
  reset-credit summary 保留最后可靠值。没有可靠基线时需要完整读取。
- reset-credit 只以 `availableCount` 作为权威数量，并保留
  `Unavailable`、`Empty`、`CountOnly`、`PartialDetails`、`CompleteDetails` 五态。
- 失败不能清空最后有效额度数据。刷新中、数据新鲜、数据过期、离线、未认证和
  不可用需要能被 Android UI 区分。
- 原始 limit ID、reset-credit ID、token、认证响应、原始 stdout/stderr 和 RPC 错误
  正文不进入 Bridge API、日志或持久化数据。

## 阶段状态

| 阶段 | 目标 | 前置条件 | 当前状态 |
| --- | --- | --- | --- |
| P0 | 证明真实 Android ARM64 + Termux 可运行并读取 App Server | 无 | Go；真实 ARM64/Termux 三次复验完成 |
| P1 | 建立 Termux Bridge 和本地只读接口 | P0 Go | In progress |
| P2 | 用假数据验证 Android 最小额度 UI | P0 Go；可与 P1 并行设计，但发布验收仍受 P0 约束 | Blocked；等待 P0 |
| P3 | 将 Android App 接入真实 Bridge 数据 | P0、P1、P2 Go | Blocked；等待 P0 |

P0 未通过时，P1、P2、P3 均保持 blocked；不能用模拟器上的 UI 成功替代 P0。

## P0：App Server 可行性（硬门禁）

P0 必须在真实 Android ARM64 手机上完成，不以 Windows、桌面 Linux 或 Android
模拟器结果代替。验证记录至少包含设备 ABI、Termux 环境、验证日期和完整的有界
进程生命周期结果。

P0 必须覆盖以下顺序和能力：

1. 在 Android ARM64 的 Termux 中启动 `codex app-server --stdio`。
2. 完成 `initialize`。
3. 探测 `account/read`。
4. 完成或明确分类处理 `account/rateLimits/read`。
5. 使用手机本地认证完成验证；不把 token 复制到 Android App、Bridge 参数、日志或
   明文配置中。
6. 证明认证持久化：首次配置后，重新启动 App Server 至少两次仍能使用本地认证，
   不要求每次交互式登录。
7. 连续三次完整执行上述启动、握手和读取流程并成功结束；每次都使用新的 App
   Server 进程。
8. 关闭 stdin、超时或主动停止后，没有遗留 App Server 或其子进程。
9. 记录 Codex 构建来源、版本、commit/tag 和 SHA-256。若上游构建没有公开其中
   某项，必须记录为明确的 unavailable 及原因，不能填入 unknown 后继续验收。

P0 的数据门禁有两个明确例外：

- `platformFamily` 和 `platformOs` 是否非空不是 P0 硬门禁；结果需要记录，但不能
  仅因这两个字段缺失判定 Android 运行时不可行。
- 真实 `account/rateLimits/read` 响应不要求至少包含一个窗口。零窗口、字段缺失、
  不可用或未认证都必须以可区分的结构化状态表达；无法分类、挂死、非法 envelope
  或悄悄变成零值才是失败。

默认 Go 标准要求 `account/read` 被实际探测并得到成功或明确协议结果；若当前版本
明确不支持该方法，P0 默认保持 blocked，不能用额度读取成功来掩盖探测缺口。是否将
`account/read` 纳入正式 Bridge allowlist，必须另行更新协议合同和请求序列测试。

## P1：Termux Bridge

P1 交付一个个人使用的、只读的 Termux Bridge。它只对 App Server 负责，Android
只依赖 Bridge 的版本化本地状态接口。

- 管理 App Server 子进程、stdin/stdout JSONL、stderr 排空、超时、EOF、有限重连和
  子进程回收。
- 只发送经过协议合同批准的只读请求；不发送登录、购买、额度消费或 reset-credit
  消费请求。
- 输出归一化额度窗口、刷新/过期/离线/不可用状态和 reset-credit 五态。
- 默认端口为 `43127`，允许配置，绑定地址固定为 `127.0.0.1`。
- 当前最小本地接口为 `GET /v1/status` 和 `GET /healthz`；状态带有
  `schemaVersion`，不暴露原始 JSON-RPC、token 或 opaque ID。
- 先用匿名 fixture 和 fake upstream 验证，再接入 P0 已验证的真实 App Server。

P1 的 Go 条件是：JSONL、动态窗口、缺失值、稀疏合并、reset-credit 五态、进程回收
和本地 HTTP 状态接口都有离线测试；Bridge 不暴露原始响应或认证材料。

## P2：Android 假数据 UI

P2 只验证 Android 客户端对 Bridge 状态模型的投影，不连接 Termux、不读取真实账号，
也不构建完整产品 UI。最小范围包括多个动态窗口、百分比可靠性、重置时间、过期/离线
表现和 reset-credit 五态。

P2 的输入只能是版本化 Bridge 状态 fixture。UI 不应依赖 App Server 字段名、原始
JSON-RPC envelope 或 Windows 专属状态。

P2 的 Go 条件是：假数据单元测试和最小 UI 测试通过；零窗口、不可用、缺失值和旧数据
状态都能明确展示；没有真实 Bridge 时应用仍能启动并验证状态投影。

## P3：真实数据接入

P3 将 P2 的 Android App 连接到 P1 Bridge，并在真实 Android ARM64 手机上验证：

- Bridge 启动、停止、重启和 App 重启后的读取行为。
- App Server 断开、请求超时、认证不可用和网络恢复时的状态转移。
- 稀疏通知与完整读取之间不会丢字段、倒序覆盖或清空最后有效数据。
- 真实账户响应可以只有零个窗口、缺少 reset-credit 或缺少部分 metadata，Android
  仍能表达明确状态。
- Android 只读，Bridge 不执行任何账户写操作。

P3 的 Go 条件是：真实 ARM64 手机上能够从 `127.0.0.1` 取得 Bridge 状态，数据和
 连接状态均可解释，异常恢复不会遗留子进程或产生伪造零值。

## Go / No-Go 条件

### Go

- P0 的 ARM64、启动、认证持久化、连续三次运行、子进程回收和来源指纹记录全部完成。
- `initialize`、`account/read`、`account/rateLimits/read` 的结果都能在期限内被分类。
- 额度读取的零窗口和不可用结果可以被结构化表达。
- P1/P2 的离线验证通过，P3 的真实手机验收通过。

### No-Go

- App Server 无法在真实 Android ARM64 上启动，或只能依赖未记录来源的二进制。
- 本地认证不能持久化，或必须把凭据复制给 Android App/Bridge。
- 任一连续运行会挂死、无限等待或留下遗留子进程。
- `account/read` 默认探测失败且没有明确的协议决策来接受该缺口。
- malformed、missing、null 或 sparse 数据被静默解释成零值，或覆盖最后有效状态。
- Bridge 需要公开监听、远程中转或账户写请求才能工作。

## 非目标

- 不替换、不重构、不共享修改当前 WinUI 生产实现。
- 不追求单 APK、应用商店上架、签名、安装器或多用户支持。
- 不做远程访问、云端 Bridge、跨设备同步或完整的认证管理界面。
- 不在 P0–P3 内实现完整设置、通知中心、系统级后台服务或 Widget。
- 不因为真实响应没有窗口、没有 reset-credit 或缺少 platform 字段而伪造业务数据。

## Later

以下内容全部排在 P3 之后：

- 后台刷新和 Android 生命周期/Doze 适配。
- Android 通知和额度阈值提醒。
- Termux:Boot、自动启动和长期驻留策略。
- Home-screen Widget、快捷方式和系统分享入口。
- 单 APK、Bridge 打包、自动安装或应用商店发布。
- 更完整的缓存、诊断、升级和设备兼容性矩阵。

## 未验证假设

- 当前 Codex 构建及其 Node/npm 依赖支持 Android ARM64 和 Termux。
- Termux 中的本地认证路径可被 App Server 使用，并能在进程重启后保持有效。
- 当前 App Server 在 Android 上提供 `account/read`，且其响应可安全纳入 P0 判定。
- `account/rateLimits/read` 和 `account/rateLimits/updated` 的真实 shape 与当前合同
  足够兼容。
- Termux 与普通 Android App 共享 `127.0.0.1` 网络空间，且明文 loopback HTTP 不被
  Android 网络安全策略拦截。
- Termux 后台运行不会被系统休眠或回收；端口 43127 不会发生冲突。
- Python/标准库或最终选定的 Bridge 运行时在目标手机上可用且足够稳定。
- Kotlin 序列化方案能够区分字段缺失与显式 null。
- Android 工具链、JDK、Gradle、Kotlin 和最低 SDK 尚未固定。
- 手机时钟、时区、Unix 时间戳和过期判定满足当前展示语义。
- P0–P3 不需要后台常驻、通知、Widget 或 Termux:Boot 才能完成个人使用目标。
