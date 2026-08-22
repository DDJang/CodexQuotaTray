# CodexQuotaTray 协议合同

## 权威来源

App Server 协议基线以 [`schemas/CODEX_VERSION`](../schemas/CODEX_VERSION) 为唯一版本来源，wire
shape 以对应生成 schema 为准。本文只记录两个客户端实际消费的只读子集、规范化和持久化合同。
未知字段必须忽略；实际使用字段缺失、null 或 malformed 时不能生成业务零值。

## WinUI App Server

WinUI 启动 `codex app-server --stdio`，通过 UTF-8 JSONL 通信。当前 outbound 只有：

1. `initialize` request，携带 `clientInfo`；
2. `initialized` notification；
3. `account/rateLimits/read` request，params 为 `null`。

WinUI 不发送登录、`account/read`、token refresh、购买或其他账户写请求；认证完全由本机 Codex
CLI 管理。`initialize` 只要求非空 `platformFamily`、`platformOs`，可选 `userAgent` 仅用于提取
版本 token。

`account/rateLimits/read` 当前消费：

```text
rateLimits: RateLimitSnapshot?
rateLimitsByLimitId: map<string, RateLimitSnapshot>?
rateLimitResetCredits: RateLimitResetCreditsSummary?

RateLimitSnapshot:
  limitId: string?
  limitName: string?
  planType: string?
  primary: RateLimitWindow?
  secondary: RateLimitWindow?

RateLimitWindow:
  usedPercent: integer?
  windowDurationMins: integer?
  resetsAt: Unix seconds?
```

非空 `rateLimitsByLimitId` 优先于 legacy `rateLimits`。缺少 `usedPercent` 的窗口不是有效额度窗口；
越界值只为显示 clamp 并标记不可靠。

`rateLimitResetCredits.availableCount` 是数量权威来源；`credits` 列表长度不能代替它。客户端只从
有效 `expiresAt` 中选择最早到期时间，原始 ID 不进入 UI 或持久化。

入站只消费 `account/rateLimits/updated`。稀疏通知按 ingress sequence 合并，只覆盖明确出现且非
null 的字段；无基线、溢出或无法安全定位时完整补读。RPC error 正文、stderr 和 raw JSON 不进入
日志或磁盘。

## Android Direct HTTPS quota

Android 通过设备代码 OAuth 获得 App 私有凭据，额度请求为：

```text
GET https://chatgpt.com/backend-api/wham/usage
Authorization: Bearer <access-token>
Accept: application/json
ChatGPT-Account-Id: <account-id>  # 仅可靠时发送
```

消费 `plan_type`、`rate_limit.primary_window`、`secondary_window` 和
`additional_rate_limits[]`。窗口字段 `used_percent`、`reset_at`、`limit_window_seconds` 均可
缺失。若响应含 `rate_limit_reset_credits.available_count > 0`，再用同一组 OAuth 请求只读的
`GET https://chatgpt.com/backend-api/wham/rate-limit-reset-credits` 获取明细；数量为 0 时不请求明细。
明细失败不影响 usage 成功，`availableCount` 仍为权威值，`credits=null` 与成功返回的 `[]`
保持可区分。401/403 允许一次 refresh 恢复后重试；普通刷新不无条件 refresh。

错误分为登录、网络、服务端和非法响应。Direct 永远优先；只有 `NETWORK`、已配对 Windows 且
Wi-Fi LAN 可用时才读取 `/v1/quota`。Windows 失败后仍呈现原 Direct 网络错误。

## 额度规范化与持久化

共同规范化规则：

- 动态保留全部窗口，不按槽位猜周期；
- `remainingPercent = 100 - clamp(usedPercent, 0, 100)`；
- 缺失百分比、时长和重置时间保持未知；
- malformed 新响应不清空最后有效状态。

额度用户展示投影只接受 canonical bucket `codex`；`gpt-reserve` 和未知 bucket 保留在原始
规范化数据中，但不进入 WinUI、Android 前台或小组件。没有 `codex` 时保持空/不可用语义，
不把未知 bucket 映射或回退为 `codex`。

WinUI `QuotaCacheDocument` 只保存格式版本、成功时间、套餐、最多 32 个归一化窗口以及
reset-credit 的可展示摘要/明细投影；`credits=null` 与 `[]` 保持可区分，并不保存完整
reset-credit ID、账户、CLI 路径、warning/RPC 正文或 raw JSON。Windows LAN snapshot 通过同样的
`availableCount`/`credits` 投影把只读信息传给 Android；旧 snapshot 缺少该字段时按未知处理。

WinUI 可按用户设置将 `TokenUsageSnapshot` 保存为 `token-usage-cache.json`。该缓存只含 schema
版本、生成时间、时区、Token 摘要和最多 366 条按日数字聚合，不含 session ID、文件路径、账户、
prompt、response、工具内容或原始 JSONL；关闭“保存统计缓存”后删除，读取异常时直接忽略。

Android `QuotaSnapshotStore` 保存最后成功的脱敏产品快照：套餐、quota state、数据更新时间、
来源及窗口的 bucket、本地标识/名称、百分比、时长和重置时间，以及 reset-credit 的权威数量和
可展示明细投影（`credits=null` 与 `[]` 保持可区分，不保存完整 reset-credit ID）。它另外保存本机
`lastSuccessfulRefreshAtMillis` 以保持缓存兼容，但前台 freshness 使用进程内最后一次自动尝试
时间，不替代数据更新时间。快照不含 OAuth
凭据、HTTP body/header、账户 ID、错误正文或历史序列；退出登录会清除快照。

Android Token cache 只保存 Windows 返回的聚合 schema，并绑定 pairing 的设备 identity。解除或
替换 pairing 时清除；提交前 pairing 改变时丢弃结果。

## Windows LAN schemaVersion 1

Windows 只在用户启用同步后，以二维码/DNS-SD 携带的实际私人 IPv4 与端口提供只读接口。请求
必须包含 pairing Bearer；无效 secret 返回 401，非 GET 返回 405，未知路径返回 404，响应包含
`Cache-Control: no-store`。

### `GET /v1/token-usage`

```text
schemaVersion: 1
generatedAtUtc: ISO-8601
sourceTimeZone: string
summary:
  todayTokens, last7DaysTokens, last30DaysTokens, lifetimeTokens
  peakDailyTokens, peakDate, activeDays, currentStreak, longestStreak
days[]:
  date, totalTokens
  inputTokens?, cachedInputTokens?, outputTokens?, reasoningTokens?
```

只返回最近 365 天日聚合和全历史 summary；不得包含 session ID、路径、账号、prompt、response、
工具内容或原始 JSONL。

Token Usage 普通请求采用 stale-while-revalidate：无缓存时等待首次真实扫描；有缓存且年龄小于
`minimumScanInterval`（默认 60 秒）时立即返回缓存且不扫描；有缓存但已 stale 时仍立即返回当前
缓存，同时触发 process-local single-flight 后台扫描刷新，多个 stale 普通请求不得并发启动多个
扫描。缓存命中的 `generatedAtUtc` 必须来自实际扫描结果，不得伪造成当前时间。

用户手动同步可在鉴权后使用 `GET /v1/token-usage?refresh=force`，等待真实扫描完成后返回；该参数
只适用于 Token Usage。Android 启动、回到前台和后台同步不携带 `force` 参数。

### `GET /v1/quota`

返回 Windows Runtime 已维护的最后成功快照，不主动刷新 App Server；无快照返回 503：

```text
schemaVersion: 1
generatedAtUtc: ISO-8601
planType: string?
quotaState: available | zero_windows
windows[]:
  bucketId?, limitId?, limitName?, planType?, sourceSlot
  usedPercent?, remainingPercent?, percentageReliable?
  windowDurationMins?, resetsAt?
```

### Pairing 与发现

Pairing 具有稳定随机 `deviceId` 和高熵 `pairingSecret`。二维码格式为：

```text
codexquota://pair?deviceId=<uuid>&host=<private-ip>&port=<port>&token=<secret>&name=<optional>
```

Android 校验 scheme、deviceId、RFC1918 IPv4、端口和 secret，不接受公网、hostname、loopback、
redirect 或其他 scheme。DNS-SD 服务类型为 `_codexquota._tcp`，metadata 只含 deviceId、名称和
端口。仅 offline 类连接失败可发现并重试相同 deviceId；401 必须要求重新配对。

## 变更控制

协议升级必须同时更新 schema baseline、DTO、规范化、匿名 fixture、本合同和完整离线测试。
服务端新增写能力不自动授权客户端调用。
