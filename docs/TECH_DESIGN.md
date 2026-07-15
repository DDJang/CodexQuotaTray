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
JsonlTransport ──► JsonRpcClient ──► ProtocolDecoder ──► QuotaProjector
                                            │
                                            ▼
                                      AppStateReducer
                                            │
                         ┌──────────────────┴──────────────────┐
                         ▼                                     ▼
                 NonSensitiveCache                    Future UI/Notifications
```

### 4.1 组件职责

- **当前实现 — `app_server`**：发现 Codex、启动子进程、读写 JSONL framing、排空 stderr、关闭和回收进程；不负责响应路由。
- **当前实现 — `supervisor`**：后台轮询子进程退出状态，发布连接代次，处理显式 transport 恢复请求，并执行有界 restart/backoff。
- **当前实现 — `json_rpc`**：生成连接内唯一 ID，维护多个 pending request，按 ID 分派乱序响应，区分成功、错误、通知和脱敏诊断，并统一处理请求超时与 stdout EOF。
- **当前实现 — `protocol`**：只定义握手、账户读取、额度读取及更新通知所需 wire types。
- **当前实现 — `quota`**：优先读取多 bucket 视图，将窗口归一化为与 `primary`/`secondary` 语义无关的 domain model。
- **当前实现 — CLI orchestration**：通过 `json_rpc` 发起请求、消费通知，并负责人类可读输出和退出码。
- **当前实现 — `state`**：纯 `AppStateReducer` 是唯一状态转换入口，线程安全内存 store 返回 owned snapshot；UI 不得直接消费 wire JSON。
- **当前实现 — `runtime`**：把 supervisor 连接代次、握手、并发只读 RPC、refresh coordinator、稀疏通知和 reducer 串为一个长期后台 worker；公开接口只暴露 normalized snapshot、刷新触发和幂等 shutdown report。
- **拟议设计 — cache/UI/notification adapters**：只读取归一化状态，不持有 transport 或认证数据。

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

- **当前实现** P0 首次快照完成后按 CLI 参数限时监听通知。
- **当前实现** supervisor 同一时间只维持一个 App Server 子进程；重启后发布新连接代次，旧 JSON-RPC pending 不跨进程重放。
- **已确认** P0 关闭 stdin 后子进程在三秒内自然退出，退出码为 0。
- **当前实现** 三秒后仍未退出才 kill 并 wait；强制终止被视为清理失败。
- **当前实现** `AppServer` 与 supervisor shutdown 均幂等；重复调用返回第一次的相同脱敏报告。
- **拟议设计** Windows 关机和会话退出信号在托盘里程碑接入同一 shutdown path。

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
- 主动读取请求最小间隔 10 秒；并发刷新合并为一个 in-flight 请求。
- 认证模式不支持和 schema version mismatch 不进行无意义的快速重试。

### 6.3 Refresh coordinator

- **当前实现** 同一时间最多一个 quota refresh；并发触发合并为一个最高优先级 pending reason。
- **当前实现** 主动刷新最小间隔为 10 秒，请求 deadline 为 15 秒，无事件时每 10 分钟生成 fallback refresh。
- **当前实现** startup、manual、rate-limit notification、resume、network restored、card opened 和 fallback 使用同一调度路径。
- **当前实现** 未知或重复 completion 不修改当前 in-flight；request ID 单调且不回绕。
- **已确认** 纯虚拟时间测试重放 24 小时，始终最多一个 in-flight，并只产生 1 次 startup 加 144 次 fallback。
- **当前实现** runtime executor 每次逻辑 refresh 同时发出 `account/read` 与 `account/rateLimits/read`，按请求 ID 等待各自响应；通知先执行安全稀疏合并，再经最小间隔调度一次权威完整补读。
- **未实现** Windows resume/network/card-open 事件 adapter；它们属于托盘/系统集成里程碑。

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

所有已实现状态变化均通过纯 reducer 完成；事件携带显式时间，reducer 不读取系统时钟或执行 I/O，因此可使用 fixture 完整重放。

### 7.3 当前 reducer 失败语义

- 刷新开始只把 data state 设为 `refreshing(previous)`，不会删除 quota snapshot。
- transport failure 进入 `offline`；其他读取失败按最后成功时间保持 `fresh` 或进入 `stale`。
- 成功数据满 15 分钟后由显式 `Tick` 转为 stale；缺失字段不会生成 0 或 100。
- 不完整 read、无基线 patch 和多 bucket 歧义 patch 均保留最后有效快照并产生匿名 warning code。
- 明确切换到未登录、API Key、Bedrock 或不支持账户模式时清除旧 ChatGPT quota，避免跨账户模式展示旧数据。
- sparse patch 在私有 typed snapshot 上合并；公开 store snapshot 只包含 normalized domain state。

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

- **已确认** 10 个 fixture/parser 测试、7 个 JSON-RPC fake transport 测试、2 个 backoff 单元测试、8 个 fake-process supervisor 测试、9 个 reducer 测试、9 个 refresh coordinator 测试、3 个 version compatibility 单元测试和 6 个 runtime fake-process 测试完全离线通过。
- 覆盖 ChatGPT、API Key、Bedrock、未登录、single/dual/multi bucket、未知时长、缺失字段、越界百分比、malformed JSON 和稀疏合并。
- JSON-RPC 测试覆盖唯一 ID、多个 pending、乱序响应、RPC error、通知、timeout、EOF、未知/重复 ID、null result、非法 JSON 和非法 envelope。
- supervisor 测试覆盖非零退出恢复、restart budget、启动失败、显式恢复、stderr flood、正常 EOF、强制回收和幂等 shutdown。
- reducer 测试覆盖 process/auth/data 状态、失败保留、15 分钟 stale、完整替换、稀疏更新、歧义拒绝和 owned store snapshot。
- runtime 测试通过真实本地 stdio pipe 覆盖乱序并发响应、启动快照、手动刷新合并、通知稀疏合并与完整补读、单代次崩溃恢复和幂等关闭。
- version 测试覆盖生成记录读取、精确 match、pre-release mismatch 与 unreported；mismatch runtime 测试同时证明 quota 仍可 best-effort 投影。
- 请求序列测试证明 runtime 只使用四个允许 method，通信层统一注入 request ID。
- 脱敏实跑证明真实 quota 可读、默认发现 `codex.cmd` 可用且 stdin close 能干净退出。
- 90 秒真实 runtime soak 保持 generation 0 / fresh / 单窗口，完成 1 次读取、0 failure、0 restart、0 forced termination、0 protocol diagnostic；退出后进程查询无遗留 App Server。

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
