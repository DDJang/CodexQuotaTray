# Codex App Server API Contract

## 权威来源与适用范围

本合同描述 CodexQuotaTray 两个客户端共享的最小只读协议子集，不复制完整 schema。

当前 WinUI 消费 `initialize`、`initialized`、登录、`account/read` 和
`account/rateLimits/read` 所需子集。Android 的 P0/P0.5 App Server 探测曾验证过同一
组合同，但 Android 当前日常产品路径使用独立的 OAuth + Direct HTTPS usage API，
不再启动 App Server。本文的稀疏通知、reset-credit、缓存和完整规范化章节主要描述
WinUI 已实现能力；除字段缺失和动态额度窗口语义外，不自动成为 Android 后续目标。
Android 产品范围以
[Android Roadmap](ANDROID_ROADMAP.md) 为准。

- 协议基线版本以 [`schemas/CODEX_VERSION`](../schemas/CODEX_VERSION) 为唯一来源。
- Wire shape 以该版本对应的 `schemas/codex-<version>/` 生成文件为准。
- WinUI DTO、规范化和合并策略以 `Core/Protocol` 实现为准；Android 使用独立 Kotlin
  实现，但必须保持本文的 envelope、字段缺失和额度语义。
- 运行时不读取 `schemas/` 目录；schema 只用于升级审计、fixture 和合同校准。

服务端新增未知字段应被忽略。应用实际使用的字段缺失、为 null 或 malformed 时，必须进入明确的不可用、部分可用或协议失败路径，不能静默生成业务零值。

## Transport

### WinUI

- 子进程为 `codex app-server --stdio`。
- stdin/stdout 使用 UTF-8 JSONL，一行一个完整消息。
- Wire envelope 不要求 `jsonrpc` 字段。
- 请求 ID 在单个连接内唯一，由 transport 生成和路由。
- stdout EOF、读写失败或连接关闭会使 pending request 以受控 transport error 完成。
- 超时请求从 pending 集合移除；迟到响应不能恢复请求或覆盖其他请求。
- 非法 JSON、非法 envelope、未知或重复 ID 只产生脱敏诊断，不输出原始消息。
- stderr 被持续排空，但正文不进入日志或持久化。

### Android 历史 App Server 探测

- P0/P0.5 曾从 APK native library 启动 Android ARM64 Codex runtime，并通过本机
  WebSocket 验证 App Server。
- 该路径保留在历史结果和 Python 诊断中，不是当前日常 APK 的数据路径。

### Android 当前 Direct HTTPS

- OAuth device-code 登录和 refresh token 只保存在 App 私有存储；旧
  `filesDir/codex-home/.codex/auth.json` 只作为一次性迁移输入，旧文件保留。
- 日常额度读取是：

  ```text
  GET https://chatgpt.com/backend-api/wham/usage
  Authorization: Bearer <access token>
  Accept: application/json
  ChatGPT-Account-Id: <account id>  // 有可靠值时才发送
  ```

- usage API 的 401/403 触发一次 refresh token 恢复后重试；普通启动和普通刷新不
  无条件 refresh。
- 当前消费 `plan_type`、`rate_limit.primary_window`、`secondary_window` 和顶层
  `additional_rate_limits[]` 中的动态窗口。窗口的 `used_percent`、`reset_at` 和
  `limit_window_seconds` 均可缺失；缺失不转换为零。
- Android 正常路径不启动 App Server，不使用 WebSocket、JSONL、Termux Bridge、
  HTTP 中转或 `ProcessBuilder`。WorkManager 只调度同一个 Direct HTTPS 读取函数。

请求、成功响应、错误响应和通知的最小 envelope 分别为：

```json
{ "method": "method/name", "id": 1, "params": {} }
```

```json
{ "id": 1, "result": {} }
```

```json
{ "id": 1, "error": { "code": 123, "message": "<suppressed>" } }
```

```json
{ "method": "method/name", "params": {} }
```

RPC error message 只验证 shape，不向业务层传播或保存正文。

## WinUI 允许的 App Server 消息

当前 outbound allowlist 只有三条消息：

| 顺序 | Method | 类型 | Params |
| ---: | --- | --- | --- |
| 1 | `initialize` | request | `clientInfo` |
| 2 | `initialized` | notification | 无 |
| 3 | `account/rateLimits/read` | request | `null` |

应用不发送账户登录、token refresh、购买、重置卡消费或其他账户写请求。任何新增 outbound method 都需要独立更新协议合同、隐私评审和请求序列测试。

### initialize

当前 `clientInfo` 为：

```json
{
  "name": "codex_quota_tray_winui",
  "title": "CodexQuotaTray WinUI",
  "version": "<ProductVersion>"
}
```

`version` 来自应用程序集的 `ProductVersion`，不在协议代码或本文重复维护产品版本。

WinUI 客户端从初始化结果消费：

- `platformFamily`：必须为非空字符串；
- `platformOs`：必须为非空字符串；
- `userAgent`：可选，仅用于提取非敏感 runtime 版本 token。

初始化结果为空或缺少必需平台字段时进入 protocol error。完整 user-agent、平台路径和原始结果不进入 UI 或磁盘。

### initialized

初始化 request 成功并通过最低字段验证后，客户端发送无 params 的 `initialized` notification。

### account/rateLimits/read（WinUI 与历史 Android P0/P0.5 探测）

请求 params 为 `null`。当前响应 DTO 消费：

```text
rateLimits: RateLimitSnapshot?
rateLimitsByLimitId: map<string, RateLimitSnapshot>?
rateLimitResetCredits: RateLimitResetCreditsSummary?
```

非空 `rateLimitsByLimitId` 优先于 legacy `rateLimits`。当前 `RateLimitSnapshot` 消费：

```text
limitId: string?
limitName: string?
planType: string?
primary: RateLimitWindow?
secondary: RateLimitWindow?
```

当前 `RateLimitWindow` 消费：

```text
usedPercent: int64?
windowDurationMins: int64?
resetsAt: int64?  // Unix seconds
```

窗口对象缺少 `usedPercent` 时不会产生有效额度窗口。越界百分比会 clamp 到显示范围，同时标记为不可靠；缺失时长和重置时间保持未知。

## Reset-credit contract

`rateLimitResetCredits.availableCount` 是可用重置卡数量的唯一权威来源。Schema 在 summary 对象存在时要求该字段；客户端仍对缺失、null、负值和 malformed 数据进行防御处理。

当前消费的 summary 子集：

```text
availableCount: int64?
credits: RateLimitResetCredit[]?

RateLimitResetCredit:
  id: string?          // opaque，仅 typed parsing
  status: string?
  expiresAt: integer Unix seconds | null | invalid
```

`credits` 可以为 null、空或只包含部分明细，列表长度不能代替 `availableCount`。客户端只从有效数值型 `expiresAt` 中选择最早已知到期时间；无效时间被忽略，不使整个额度响应失败。

展示状态映射：

| 条件 | `ResetCreditKind` | 语义 |
| --- | --- | --- |
| 顶层字段缺失、summary 缺失或数量缺失 | `Unavailable` | 当前没有可靠重置卡信息 |
| 权威数量为 0 | `Empty` | 明确可用 0 张 |
| 数量大于 0，无有效到期时间 | `CountOnly` | 仅数量可靠 |
| 有到期时间，但明细数不等于权威数量 | `PartialDetails` | 数量可靠、到期摘要不完整 |
| 有到期时间，明细数等于权威数量 | `CompleteDetails` | 数量及当前明细摘要完整 |

负数数量被视为数据问题并记录匿名 issue，展示值限制为零。Opaque reset-credit ID 不进入归一化状态、UI、日志或持久化。

## 服务端通知与稀疏合并

客户端只消费 `account/rateLimits/updated`。通知 params 使用与额度读取相同的 `RateLimitsResponse` 子集，并按 ingress sequence 排序应用。

合并规则：

1. 只有通知中实际出现且非 null 的 snapshot/window 字段覆盖基线。
2. 未出现的 legacy snapshot、bucket、窗口字段和 reset-credit summary 保留原值。
3. 通知显式包含 `rateLimitResetCredits` 时，使用通知值；否则保留完整读取的摘要。
4. 已有可靠基线时，在基线上应用 sparse patch。
5. 没有基线时，只有可独立识别且窗口字段完整的通知才能成为独立快照；否则由协调器补充完整读取。
6. 通知缓冲区溢出、客户端 generation 变化或无法安全应用时，丢弃不可靠基线并补读。
7. 失败或无法安全定位的数据不得覆盖最后有效快照。

## 归一化状态

当前规范化额度快照包含：

```text
windows[]:
  localKey
  alertKey
  limitName?
  sourceSlot
  usedPercent
  remainingPercent
  percentageReliable
  windowDurationMinutes?
  resetAtUtc?

resetCredits:
  kind
  availableCount?
  earliestKnownExpiry?

planType?
issueCount
resetCreditsFieldPresent
availableCount?
creditDetailCount?
```

`localKey` 和 `alertKey` 是本地派生标识。原始稳定 limit ID 只参与 SHA-256 派生，不直接进入 UI 或持久化；没有稳定 ID 时使用 source slot、时长和序号构成 fallback。

规范化和投影遵循：

- 不根据 `primary`、`secondary` 命名周期；
- 优先显示服务端 `limitName`，否则根据时长生成名称；
- `remainingPercent = 100 - clamp(usedPercent, 0, 100)`；
- 缺失值保持未知，不能静默替换为零；
- malformed 响应不能清空最后有效状态；
- runtime 版本差异不单独阻止 best-effort 只读读取，能力由实际握手和 read 结果决定。

## 持久化合同

以下额度缓存合同描述 WinUI 的归一化缓存。当前 Android 不持久化完整额度缓存；
Android 只持久化 OAuth Store 和提醒去重状态，usage 响应直接投影到 UI。

磁盘缓存是归一化状态的最小投影，不是 wire response archive。当前 `QuotaCacheDocument` 包含：

```text
formatVersion
lastSuccessUtc
planType?
windows[]:
  sourceSlot
  usedPercent
  remainingPercent
  percentageReliable
  windowDurationMinutes?
  resetAtUtc?
resetCreditAvailableCount?
resetCreditEarliestExpiryUtc?
```

缓存最多保存 32 个窗口，单个 JSON 文件最大 64 KiB。不支持的格式、超限、损坏或无法解析的文件被拒绝，不从缺失字段制造零值。

缓存不保存：

- CLI 或 App Server 版本；
- 原始 `limitId`、`limitName` 或 reset-credit ID；
- token、Cookie、邮箱、账户 ID 或 `codexHome`；
- warning 正文、RPC error 正文、stdout/stderr 或 raw JSON。

缓存恢复后只代表最后已知数据；实时读取成功后才以新的服务端快照替换。关闭缓存会清除额度缓存，但不清除独立的提醒防重复状态。

## 隐私和变更控制

Fixture 必须人工构造或脱敏，不得复制真实认证数据、账户标识或原始 response blob。离线 parser 测试不得启动真实 Codex、读取凭据或访问网络。

协议升级流程：

1. 更新 `schemas/CODEX_VERSION` 指向经确认的 CLI 基线。
2. 重新生成对应 schema bundle。
3. 审查本合同使用的消息和字段 diff。
4. 更新 DTO、规范化、合并、匿名 fixture 和回归测试。
5. 完整离线验证通过后，才可显式选择真实资源 smoke。

Schema 新增写方法或 reset-credit 明细不自动授权客户端调用。MVP 的只读边界独立于服务端能力，任何写操作都需要单独产品决策和安全评审。
