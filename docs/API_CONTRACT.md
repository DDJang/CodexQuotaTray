# Codex App Server API Contract

合同状态：0.137.0 已验证子集
Schema source：`schemas/` 目录的 stable generator 输出
适用范围：CodexQuotaTray 只读额度客户端

## 1. 合同解释规则

- **Confirmed** 表示生成 schema 或 P0 实跑已验证。
- **Client policy** 表示 CodexQuotaTray 的本地约束，不代表 App Server 的全局能力。
- 生成 schema 是 wire shape 的权威来源；本文只描述应用使用的最小子集。
- 未出现在 0.137.0 schema 中的字段或方法不得由客户端猜测。
- 服务端新增未知字段必须忽略；已知必填字段缺失必须产生 protocol error，不能补默认业务值。

## 2. Transport contract

### 2.1 Framing

- **Confirmed** transport 为 `codex app-server --stdio`。
- stdin 和 stdout 均为 UTF-8 JSONL：一行一个完整消息，以换行结束。
- **Confirmed** wire envelope 不包含 `jsonrpc` 字段。
- 客户端必须持续读取 stdout 和 stderr，避免任一 pipe 填满造成死锁。
- **Client policy** stderr 内容默认丢弃，仅记录“存在诊断输出”这一布尔事实。

### 2.2 Envelope

请求：

```json
{ "method": "method/name", "id": 1, "params": {} }
```

成功响应：

```json
{ "id": 1, "result": {} }
```

错误响应：

```json
{ "id": 1, "error": { "code": 123, "message": "<suppressed>" } }
```

通知：

```json
{ "method": "method/name", "params": {} }
```

请求 ID 在单个连接内唯一。**Current P1 client policy** 使用从 0 开始的单调递增 `i64`；整数耗尽时拒绝新请求，不回绕复用。未来 reconnect 建立新连接时可重新创建连接级计数器。

### 2.3 P1 dispatch 与失败语义

以下是当前客户端实现，不是 App Server 的额外协议保证：

- 请求写入前先以 ID 注册到 pending map，允许多个请求同时等待。
- 唯一 dispatcher 按响应 ID 完成对应 pending request，不使用到达顺序推断归属。
- 同时含 `result` 与 `error`、两者均缺失或 error shape 非法的响应属于 protocol failure；若 ID 已知，只失败该请求。
- 没有 ID 且包含字符串 `method` 的消息作为服务端通知进入独立 event queue。
- 每个请求从发送时开始计算独立 deadline；超时后删除 pending entry，迟到响应不能恢复或覆盖该请求。
- stdout EOF 或 transport 读写失败会原子关闭连接，并让所有 pending request 返回受控 transport error。
- 未知 ID、有限历史内的重复响应、非整数响应 ID、非法 JSON 和非法 envelope 只产生脱敏诊断；不 panic、不打印原始消息、不投递给其他请求。
- RPC error message 只验证其存在，不保存或向上返回；业务层仅接收本地 request ID 和 error code。

## 3. Allowed client messages

CodexQuotaTray 当前 allowlist 只有四条消息：

| 顺序 | Method | ID | Params | 类型 |
|---:|---|---:|---|---|
| 1 | `initialize` | 动态唯一 | `InitializeParams` | request |
| 2 | `initialized` | 无 | 无 | notification |
| 3 | `account/read` | 动态唯一 | `{ "refreshToken": false }` | request |
| 4 | `account/rateLimits/read` | 动态唯一 | `null` | request |

除重新读取额度外，任何新 outbound method 都必须先更新本合同、生成 schema、隐私评审和请求序列测试。
下列示例中的数字 ID 仅为说明；runtime ID 由 JSON-RPC 层生成，协议参数构造器不能指定 ID。

### 3.1 initialize

请求：

```json
{
  "method": "initialize",
  "id": 0,
  "params": {
    "clientInfo": {
      "name": "codex_quota_tray_spike",
      "title": "CodexQuotaTray P0 Spike",
      "version": "0.1.0"
    }
  }
}
```

**Confirmed schema constraints**：

- `clientInfo` 必填。
- `clientInfo.name` 和 `clientInfo.version` 必填；`title` 可空。
- `capabilities` 可空；当前客户端不声明 `experimentalApi`。

成功 result 必须包含：

```text
userAgent: string
codexHome: absolute path string
platformFamily: string
platformOs: string
```

**Privacy policy**：`codexHome` 只用于完成反序列化，不进入 domain state、日志或 UI。

### 3.2 initialized

```json
{ "method": "initialized" }
```

**Confirmed** 0.137.0 `ClientNotification` schema 只要求 `method`。P0 采用无 `params` 形式并成功完成后续请求。

### 3.3 account/read

```json
{
  "method": "account/read",
  "id": 1,
  "params": { "refreshToken": false }
}
```

`refreshToken` 为可选 bool。**Client policy** 永远显式发送 false，避免主动 token refresh；正常认证维护仍由 Codex CLI 负责。

响应合同：

```text
requiresOpenaiAuth: bool                  required
account: null | Account                   optional/null

Account.type:
  apiKey
  chatgpt      + email:string + planType:PlanType
  amazonBedrock
```

**Privacy policy**：虽然 ChatGPT wire account 的 `email` 在 schema 中必填，typed domain model 必须忽略该字段。允许保留 `type` 和 `planType`。

账户状态映射属于 **client policy**：

| Wire 状态 | Domain auth state | 额度行为 |
|---|---|---|
| `account.type = chatgpt` | authenticated | 继续解析 rate limits |
| `account.type = apiKey` | api_key | 显示 ChatGPT quota 不适用 |
| `account.type = amazonBedrock` | bedrock | 显示 ChatGPT quota 不适用 |
| `account = null` 且 `requiresOpenaiAuth = true` | unauthenticated | 提示运行 Codex 登录 |
| `account = null` 且 false | unknown/unavailable | 不猜测账户类型 |

### 3.4 account/rateLimits/read

```json
{
  "method": "account/rateLimits/read",
  "id": 2,
  "params": null
}
```

成功 result：

```text
rateLimits: RateLimitSnapshot                         required
rateLimitsByLimitId: map<string, RateLimitSnapshot>? optional/null
```

`rateLimits` 是 confirmed legacy single-bucket view；`rateLimitsByLimitId` 是 confirmed multi-bucket view。

`RateLimitSnapshot` 的全部业务字段在 schema 中均可缺失：

```text
limitId: string?
limitName: string?
planType: PlanType?
primary: RateLimitWindow?
secondary: RateLimitWindow?
rateLimitReachedType: enum?
credits: CreditsSnapshot?
individualLimit: SpendControlLimitSnapshot?
```

`RateLimitWindow`：

```text
usedPercent: int32             required when window object exists
windowDurationMins: int64?     optional/null
resetsAt: int64?               optional/null, Unix seconds
```

`CreditsSnapshot`：

```text
hasCredits: bool               required
unlimited: bool                required
balance: string?               optional/null
```

**Contract limitation**：`CreditsSnapshot` 不是 reset-credit summary；客户端不得从它推导可用重置次数。

## 4. Server notification contract

### 4.1 account/rateLimits/updated

```json
{
  "method": "account/rateLimits/updated",
  "params": {
    "rateLimits": { "<sparse RateLimitSnapshot>": true }
  }
}
```

- `params.rateLimits` 必填。
- **Confirmed schema description** 这是 sparse rolling update。
- P0 live run 未观察到此事件，因此事件到达频率、触发时机和长期可靠性尚未由实测确认。

Client merge policy：

1. 将 patch 合并到 legacy `rateLimits`。
2. 若 patch 有 `limitId`，合并到 key 或 snapshot `limitId` 匹配的 multi-bucket entry。
3. patch 没有 `limitId` 且当前只有一个 bucket 时，合并到该 bucket。
4. 只有非 null、实际存在的字段覆盖旧值。
5. window 内按 `usedPercent`、`windowDurationMins`、`resetsAt` 分字段合并。
6. patch 无法安全定位 bucket 时不覆盖其他 bucket；记录匿名 warning 或重新调用 read。

## 5. Normalized domain contract

以下 normalized state 已由当前 reducer/store 实现，供未来 cache 和 UI 使用；它不是 App Server wire schema：

```text
AccountMode = chatgpt | api_key | bedrock | unauthenticated | unknown

QuotaWindow:
  bucket_key: local stable key
  limit_id: string?
  display_name: string
  source_slot: primary | secondary
  used_percent: 0..100
  remaining_percent: 0..100
  window_duration_mins: int64?
  resets_at: UnixSeconds?
  reached_type: string?

QuotaSnapshot:
  windows: QuotaWindow[]
  plan_type: string?
  received_at: LocalInstant
  source_cli_version: string?
  reset_credit_state: unavailable_in_schema
  warnings: WarningCode[]

VersionCompatibility:
  unknown
  match(schema_version, runtime_version)
  mismatch(schema_version, runtime_version)
  unreported(schema_version)
```

Projection rules：

- 非空 multi-bucket view 优先于 legacy view。
- 不根据 source slot 命名周期。
- 有 `limitName` 时用于显示；否则根据 duration 生成 5-hour、7-day 或动态时长。
- 缺少 `usedPercent` 的窗口不进入有效列表；产生 warning，不变成 0%。
- 越界百分比只在展示计算时 clamp，并保留 warning。
- 缺少 reset time 显示 unknown，不推测服务端周期边界。
- read 或 patch 失败保留最后有效 normalized snapshot；只有明确的非 ChatGPT 账户状态才清除旧 quota。
- 多 bucket 且 patch 缺少可定位的 `limitId` 时拒绝猜测，保留旧状态并要求完整 refresh。
- runtime version 从完成握手的 App Server `userAgent` 中仅提取版本 token；完整 user-agent 不进入 normalized state。精确相同为 match，不同为 mismatch，无法解析为 unreported；mismatch/unreported 不阻止 best-effort 只读读取。

## 6. Error and availability contract

P0 CLI exit codes：

| Code | 含义 |
|---:|---|
| 0 | 至少一个有效窗口已输出，且 child clean exit |
| 1 | 进程、transport、timeout、RPC、schema 或清理错误 |
| 2 | 未登录、不适用认证模式或没有有效 quota window |

未来 stateful service 不直接暴露进程 exit code，而映射为：

```text
fresh        latest read succeeded
refreshing   read in flight; previous state retained
stale        last success older than product threshold
offline      transport/service failed; cached state may exist
unauthenticated
unavailable  no usable state or unsupported environment
```

任何失败都不得把未知值改成 0 或清空最后有效快照。

## 7. Privacy contract

### 7.1 Never log or persist

- OAuth access/refresh token、API key、cookie。
- account email、完整 account ID、完整 reset-credit ID。
- `codexHome`、原始 account response、原始 stdout/stderr。
- 未脱敏 JSON-RPC error message。

### 7.2 Allowed non-sensitive fields

- 认证模式、套餐类型。
- limit name/id（确认不是 account identifier 后）。
- used/remaining percent、window duration、reset timestamp。
- 本地更新时间、freshness、匿名 warning code。
- CLI/schema version。

### 7.3 Fixture rules

- fixture 必须人工构造或使用 `[REDACTED]` 占位。
- 不复制真实百分比、时间戳或原始 response blob。
- parser 测试不得启动 Codex、读取凭据或访问网络。

## 8. Versioning and change control

1. `schemas/CODEX_VERSION` 必须与生成 schema 的 CLI 版本一致。
2. CLI 升级时在独立变更中重新生成 stable schema。
3. 对 schema bundle 执行 diff，重点审查本合同列出的五个 message types。
4. 更新 wire types、fixtures、API contract 和 parser tests。
5. 运行离线完整测试后才允许 optional live smoke。
6. schema 新增 reset-credit 字段只表示可开始评审，不自动授权展示或消费。

## 9. Read-only enforcement

- Outbound method allowlist 必须在单一模块中集中定义。
- 测试必须断言序列化的启动序列只含四个已批准 method。
- 未经单独里程碑批准，runtime 不得构造账户登录、购买、消费或其他写方法。
- 本合同的任何写 API 扩展都需要安全评审、用户确认设计和幂等性测试；不属于当前 roadmap。
