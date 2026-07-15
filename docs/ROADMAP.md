# CodexQuotaTray Roadmap

状态日期：2026-07-15
当前完成度：P0 完成；P1 后台核心完成且 24 小时真实进程 soak 进行中；P2 Win32 host 已实现并等待 gate 汇总
原则：每个里程碑先满足 gate，再开始下一个；未列入当前 milestone 的功能不得顺带实现。

## 1. 路线图依据

### 已确认基础

- `codex-cli 0.137.0` stable schema 已生成并入库目录。
- P0 可通过 stdio App Server 读取真实 ChatGPT quota。
- 当前实测账户只返回一个 10080 分钟 `primary` window；`secondary` 为空。
- 多 bucket 与 legacy view 同时存在，当前 parser 优先 multi-bucket。
- stdin close 可使 P0 child 在三秒内自然退出。
- 当前 schema 不提供 reset-credit count 或消费方法。
- 更新通知的 shape 与稀疏语义已由 schema/fixture 确认，但 live run 未观察到实际通知。

### 跨里程碑约束

- MVP 始终只读。
- 不读取浏览器 cookie、网页 DOM 或 Codex token 文件。
- 不持久化 email、token、account ID 或 raw response。
- UI 永远只消费 normalized state，不解析 wire JSON。
- 不把 `primary`/`secondary` 硬编码为固定周期。
- reset-credit count 在协议明确提供前保持 unavailable。

## 2. P0 — App Server 技术可行性

状态：**完成**

### 目标

证明本机 Rust 程序能安全启动 App Server、完成握手、读取额度、解析更新通知并回收进程。

### 已交付

- 0.137.0 stable JSON Schema bundle。
- Rust CLI spike 和明确退出码。
- process、transport、wire、quota 四层边界。
- 7 个匿名 fixture 与 10 个离线测试。
- 脱敏 live smoke 和 PRD mismatch 记录。

### 完成标准

- fmt/check/clippy/test 全部通过。
- live smoke 输出至少一个真实 quota window 或可操作的环境解释。
- 无 raw sensitive output、无 consume method、无遗留 child。
- 结果：全部满足。

## 3. P0-DOC — 协议与架构基线

状态：**完成（本次文档任务）**

### 目标

把 P0 证据转换为可评审的技术设计、API contract 和 milestone gates。

### 交付物

- `TECH_DESIGN.md`：生命周期、恢复、状态、隐私和测试架构。
- `API_CONTRACT.md`：0.137.0 最小 wire contract、domain projection 和只读 allowlist。
- `ROADMAP.md`：后续里程碑定义与阻塞条件。

### 完成标准

- 每项行为明确标注 confirmed、current implementation 或 proposed。
- 文档不声称 reset-credit count 已可用。
- 不包含托盘 UI 实现或账户写操作。

## 4. P1 — 生产化后台核心

状态：**进行中；长期 read-only runtime 与版本检测已完成，exit gate 尚缺 24 小时真实进程 soak**
UI：不包含

### 目标

把限时 CLI spike 提升为可供未来托盘复用的长期只读 service core。

### 范围

- 长期 App Server supervisor 和 idempotent shutdown。
- 有界重启/backoff、请求合并、refresh timeout。
- 单一 `AppStateReducer` 与 process/auth/data 状态机。
- 非敏感内存 cache；磁盘 cache schema 仅在隐私评审后添加。
- runtime CLI/schema version detection。
- fake App Server integration harness。

### 已完成子项

- 连接内单调唯一 request ID 与多个 pending request。
- 按 ID 匹配乱序成功/错误响应，并将通知路由到独立 event queue。
- 独立请求 timeout、stdout EOF 批量失败和写失败关闭语义。
- 对未知 ID、重复响应、非法 JSON、非法 envelope 的脱敏容错。
- 7 个完全离线 fake transport 测试；P0 quota probe 已迁移到该通信层。
- 长期后台 supervisor、连接代次与同一时间单 App Server 进程约束。
- 1–30 秒 capped exponential backoff、0–20% jitter 和五分钟最多 5 次 restart budget。
- stderr 独立持续排空和仅布尔聚合的脱敏报告。
- 非零退出、启动失败、显式 transport 恢复、可中断 backoff 和 exhausted 状态。
- App Server/supervisor 两级幂等 shutdown，以及 8 个 fake-process 集成测试。
- 纯 `AppStateReducer`、线程安全内存 store 和显式 process/auth/data 状态。
- 失败保留最后有效 quota、15 分钟 stale 转换、认证模式隔离和 sparse patch 安全合并。
- 多 bucket 歧义 patch 拒绝猜测，以及 9 个离线状态转换测试。
- 单 in-flight refresh coordinator、10 秒最小间隔、15 秒 deadline 和 10 分钟 fallback。
- 多来源刷新合并、优先级 pending reason 和 24 小时虚拟时间调度回放。
- supervisor、JSON-RPC、refresh coordinator 与 reducer 的长期 runtime 接线。
- 每次逻辑 refresh 并发发出两个只读读取请求，并按 ID 接受乱序响应。
- 稀疏更新先安全合并、后调度完整补读；transport failure 进入有界进程恢复。
- 6 个完全离线的 stdio fake App Server runtime 测试，覆盖手动刷新合并、通知补读、崩溃恢复、版本 mismatch 和幂等退出。
- 从实际握手 App Server 提取版本 token，与固定 schema record 精确比较并暴露 match/mismatch/unreported。
- 有限时脱敏 soak harness；90 秒真实运行完成 1 次成功读取、0 failure/restart/forced termination，退出后无 orphan。
- settings 与 quota cache 分离的标准库持久化边界；cache 使用严格匿名字段白名单、64 KiB/32-window 上限、temporary/backup replace 和 stale-only restore。
- 损坏/超大/未知版本缓存不阻断 live runtime；恢复和回写由 6 个 persistence + 2 个 runtime 场景离线验证。

上述交付完成通信、进程管理、reducer、refresh 调度、RPC/state runtime adapter、版本探测和磁盘 cache 隐私 gate；90 秒 smoke 不替代 24 小时 gate，因此 P1 exit gate 仍未满足。

### 不在范围

- 托盘 icon、窗口、Windows notification、开机启动。
- reset-credit count 推断或消费。
- 用量历史图和预测。

### Entry gate

- P0-DOC 合并。
- 确认 supervisor/backoff 参数和 cache 隐私字段。

### Exit gate

- reducer transitions、进程崩溃、EOF、timeout、stderr flood 和 shutdown 有自动测试。
- 24 小时 soak 无 orphan process 或持续内存增长。
- 失败时保留最后有效 snapshot，15 分钟后准确转 stale。
- 所有单元/集成测试不需要真实账户。

## 5. P2 — Read-only Windows tray shell

状态：**实现完成、验收中；P1 24 小时 soak 与 Windows 10 smoke gate 尚未关闭**

### 目标

以 Windows 10/11 原生托盘呈现 P1 的 normalized state，不接触 App Server wire types。

### 范围

- 托盘 icon、tooltip 和只读详情卡片。
- fresh/refreshing/stale/offline/unauthenticated/unavailable 显示。
- 动态 quota windows、reset time 和最后更新时间。
- 手动刷新触发器，只调用 P1 read service。

### 已完成基础

- `AppState` → tray severity/tooltip/card rows 的纯投影；UI 不接触 protocol JSON。
- normal/caution/critical/exhausted/refreshing/offline 六种语义 icon state。
- fresh/refreshing/stale/offline/unauthenticated/API Key/Bedrock/unavailable 的显式文案。
- 20%、5%、耗尽与恢复的纯阈值 reducer，同一窗口/周期去重，首次快照静默建立基线。
- 11 个完全离线测试覆盖 PRD AC-01/02/05/06/08 的展示与提醒核心。
- 原生 Win32 tray icon、tooltip、深色只读卡片、标准右键菜单和单实例 host。
- P1 normalized state 实时接入、手动刷新、动态窗口/重置倒计时、最后更新时间和明确的错误/认证状态。
- normal/caution/critical/exhausted/refreshing/offline 使用形状不同的系统 icon，并辅以文字与颜色，不只依赖颜色。
- 非敏感 cache、提醒、开机启动和 cache 清理的最小系统菜单；`--shutdown-existing` 供正常安装/卸载清理。
- Windows 11 x64 手工 smoke 已通过 demo 刷新、菜单和退出；真实 release host 可启动并正常回收 P1 runtime。
- 当前资源基线：932,352-byte P3 release GUI；10 秒空闲主进程约 9.63 MiB working set、0 CPU 秒增量。

### 明确排除

- reset-credit 消费、购买、登录 token 处理。
- 网页抓取、Electron、浏览器运行时。
- 复杂阈值编辑和独立设置窗口；基础提醒开关已实现，完整事件 adapter 仍留给 P3。

### Entry gate

- P1 service API 稳定并通过 soak。
- UI accessibility、icon state 和 Windows framework 方案完成评审。

### Exit gate

- UI 不引用 `serde_json::Value` 或 protocol response types。
- 缺失窗口显示 unavailable，不显示虚假 100%。
- Windows 10/11 手工 smoke 通过；无后台持续动画或高频轮询。
- 资源指标完成首次测量并记录基线。

## 6. P3 — Refresh orchestration 与通知

状态：**实现完成、验收中；随 P1 24 小时 soak 关闭 live gate**

### 目标

在不增加高频请求的前提下完成事件优先、低频兜底的刷新与提醒。

### 范围

- 更新通知驱动刷新、10 分钟 fallback、睡眠/网络恢复刷新。
- 20%、5%、耗尽和恢复阈值 reducer。
- 去重、周期重置和 Windows 专注模式兼容。
- 最小设置与非敏感持久化。

### 已完成

- 稀疏 App Server 通知先合并、再经 10 秒最小间隔调度权威完整补读。
- card-open、系统自动恢复和网络恢复事件映射到同一 refresh coordinator；网络离线事件不触发无意义读取。
- 事件 burst 只保留一个 pending reason，同一时间最多一个 refresh；10 分钟 fallback 不依赖系统事件。
- 20%、5%、耗尽和跨周期恢复 reducer，首次观察静默、同窗口/周期去重、关闭后不补发旧阈值。
- Windows balloon 使用安静时段标志且无应用声音；提醒总开关和非敏感 settings 已持久化。
- 77 个离线测试通过；Windows 11 smoke 验证网络 monitor 注册/注销、单实例 card-open 与正常退出。
- P1 soak 首小时仍为 generation 0 / Fresh / schema match / 0 warning，证明无实际更新通知时 fallback 能维持权威新鲜状态。

### Entry gate

- 至少一次 extended live/soak 环境观察到更新通知，或证明 fallback 能覆盖通知缺失。
- P2 状态展示稳定。

### Exit gate

- 阈值、去重、跨周期恢复和 stale cache 均有状态转换测试。
- 主动请求最小间隔 10 秒；同一时间最多一个 refresh。
- 网络故障不会清空旧数据或产生通知风暴。

当前自动化已满足三个 exit 条目；最终状态随 P1 24 小时 soak 的 refresh/restart/orphan 汇总关闭。

## 7. P4 — Packaging 与发布准备

状态：**拟议**

### 目标

把只读 MVP 制作为可重复构建、安装、升级和卸载的 Windows 应用。

### 范围

- Release build、签名策略、安装器和自动启动选项。
- CLI 缺失/版本不兼容诊断。
- 隐私说明、日志开关和 cache 清理。
- 资源、7 天稳定性和崩溃恢复验收。

### Exit gate

- 可重复构建并记录 dependency/license 清单。
- 不打包 Chromium，不存储敏感认证数据。
- Windows 10/11 安装、升级、卸载和开机启动测试通过。
- 空闲 CPU、内存、安装体积和 7 天 soak 结果达到或解释 PRD 目标。

## 8. Reset-credit 功能门禁

状态：**阻塞，不属于只读 MVP**

只有同时满足以下条件才允许创建独立提案：

1. 新版 stable schema 明确提供权威 available count。
2. schema diff 和匿名 live observation 一致。
3. 产品重新确认展示/消费范围。
4. 消费方法、幂等键、确认 UX、审计和失败恢复完成单独威胁建模。

即使只满足读取条件，也只解锁“显示数量”；绝不自动解锁消费操作。

## 9. 推荐下一任务

保持 24 小时真实 Codex runtime soak 独立运行并记录每小时样本；同时补齐 P3 的 card-open/resume 事件 adapter 与 quiet-time 行为，再进入 P4 可重复打包和发布前验证。
