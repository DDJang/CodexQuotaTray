# CodexQuotaTray 技术设计

文档状态：P0 已验证，后续架构待实现
协议基线：`codex-cli 0.137.0` stable App Server schema
最后更新：2026-07-15

## 1. 事实、要求与设计决定

本文使用以下标签，避免把未来方案误写成 App Server 保证：

- **已确认**：由 0.137.0 生成 schema、P0 脱敏实跑或已通过测试直接证明。
- **当前实现**：P0 Rust spike 已采用的行为，但不代表服务端协议承诺。
- **拟议设计**：后续里程碑应实现的架构选择，必须经过对应里程碑评审。
- **产品要求**：来自 `PRD.md` 或 `AGENTS.md`，可能受协议能力限制。

## 2. 范围

本设计覆盖：

- Codex App Server 子进程管理。
- stdio JSONL transport 和最小协议面。
- 账户与额度响应解析。
- 稀疏通知合并、应用状态和故障恢复。
- 隐私边界、测试分层和版本兼容。

本设计不实现 Windows 托盘、弹出卡片、系统通知、设置页面、开机启动或任何 reset-credit 消费操作。

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

- **已确认** 0.137.0 stable 和 experimental schema 均未提供 `rateLimitResetCredits.availableCount`。
- **已确认** 当前 `credits { hasCredits, unlimited, balance }` 是通用 credits snapshot，不能解释为 reset-credit 次数。
- **已确认** 当前生成 schema 没有 reset-credit 消费方法。
- **设计结论** 在 schema 提供明确只读数量前，产品状态必须是“重置次数不可用”，不能显示 0，也不能根据 credits 推断。

## 4. 逻辑架构

```text
CodexProcessSupervisor
        │ child stdin/stdout/stderr
        ▼
JsonlTransport ──► ProtocolDecoder ──► QuotaProjector
                                            │
                                            ▼
                                      AppStateReducer
                                            │
                         ┌──────────────────┴──────────────────┐
                         ▼                                     ▼
                 NonSensitiveCache                    Future UI/Notifications
```

### 4.1 组件职责

- **当前实现 — `app_server`**：发现 Codex、启动子进程、读写 JSONL、排空 stderr、关闭和回收进程。
- **当前实现 — `protocol`**：只定义握手、账户读取、额度读取及更新通知所需 wire types。
- **当前实现 — `quota`**：优先读取多 bucket 视图，将窗口归一化为与 `primary`/`secondary` 语义无关的 domain model。
- **当前实现 — CLI orchestration**：管理请求 ID、超时、人类可读输出和退出码。
- **拟议设计 — `AppStateReducer`**：未来唯一允许修改应用状态的入口；UI 不得直接消费 wire JSON。
- **拟议设计 — cache/UI/notification adapters**：只读取归一化状态，不持有 transport 或认证数据。

## 5. 进程生命周期

### 5.1 状态机

拟议的长期进程状态：

```text
Stopped → Starting → Handshaking → Ready → Stopping → Stopped
              │            │          │
              └────────────┴──────────┴──► Failed → Backoff → Starting
```

### 5.2 启动

1. **当前实现** Windows 依次尝试 `codex.cmd`、`codex.exe`、`codex`；显式 `--codex-bin` 覆盖发现逻辑。
2. **拟议设计** 启动前执行版本探测，并将 runtime version 与 `schemas/CODEX_VERSION` 比较。
3. 启动 `codex app-server --stdio`，三个标准流全部 pipe。
4. 在 10 秒内完成 `initialize`。
5. 发送 `initialized`，随后发出两个只读读取请求。
6. 在 15 秒内收到两个可解析响应后进入 `Ready`。

版本不匹配不是自动可恢复错误：继续运行只能作为 best-effort，并必须展示 schema mismatch 状态；发布版默认应拒绝使用未验证的破坏性协议变化。

### 5.3 正常运行与退出

- **当前实现** P0 首次快照完成后按 CLI 参数限时监听通知。
- **拟议设计** 常驻服务只维持一个 App Server 子进程，不为每次刷新重复启动。
- **已确认** P0 关闭 stdin 后子进程在三秒内自然退出，退出码为 0。
- **当前实现** 三秒后仍未退出才 kill 并 wait；强制终止被视为清理失败。
- **拟议设计** Windows 关机、会话退出和应用退出均走同一 idempotent shutdown path。

## 6. 故障恢复

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

以下为**拟议设计**：

- 单个 App Server 实例同一时间只允许一个初始化流程。
- 重启 backoff 为 1、2、4、8、16、30 秒，之后保持 30 秒上限并加入 0–20% jitter。
- 五分钟内最多自动重启 5 次；超过后进入 `unavailable`，只响应用户重试或环境变化。
- 读取请求失败使用同样上限，但不得重启仍然健康的 App Server，除非 transport 已关闭。
- 主动读取请求最小间隔 10 秒；并发刷新合并为一个 in-flight 请求。
- 认证模式不支持和 schema version mismatch 不进行无意义的快速重试。

## 7. 状态管理

### 7.1 拟议状态模型

```text
ProcessState = stopped | starting | handshaking | ready | backoff | failed
AuthState    = authenticated | unauthenticated | api_key | bedrock | unknown
DataState    = empty | refreshing | fresh | stale | offline | unavailable

QuotaState:
  account_mode
  plan_type?
  buckets[]
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
- 稀疏通知只覆盖 `Some` 字段；null/缺失字段不清除已有元数据。
- 读取或更新失败不会清空最后有效快照。
- **产品要求** 成功数据超过 15 分钟未刷新后进入 stale；这是产品策略，不是 App Server 协议事实。

所有状态变化应通过纯 reducer 完成，以便使用 fixture 重放并测试每个转换。

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

## 9. 测试策略

### 9.1 已有覆盖

- **已确认** 10 个 fixture/parser 测试完全离线通过。
- 覆盖 ChatGPT、API Key、Bedrock、未登录、single/dual/multi bucket、未知时长、缺失字段、越界百分比、malformed JSON 和稀疏合并。
- 请求序列测试证明 runtime 只构造四条允许消息。
- 脱敏实跑证明真实 quota 可读、默认发现 `codex.cmd` 可用且 stdin close 能干净退出。

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
- P0 只证明 CLI 可行性，不证明 Windows 托盘资源、可访问性或交互目标。
