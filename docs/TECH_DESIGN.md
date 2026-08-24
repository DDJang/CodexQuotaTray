# CodexQuotaTray 技术设计

本文只记录当前架构和兼容边界。产品行为见 [PRD](PRD.md)，wire 与持久化字段见
[API_CONTRACT](API_CONTRACT.md)，发布流程见 [RELEASE](RELEASE.md)。

## Windows 架构

```text
codex app-server --stdio        chatgpt.com read-only HTTPS
        ↓ UTF-8 JSONL                    ↓
Core/Protocol ──────────────── Core/Auth/OAuth
        ↓ normalized quota/usage          ↓ same internal model
Core/Runtime ── Core/Persistence ── Core/Alerts
        ↓
Core/Presentation
        ↓
WinUI Views / Services / Interop / Themes
```

- `Core/Protocol` 管理 CLI 定位、子进程、JSONL transport、DTO、通知和规范化。
- `Core/Auth` 管理 Windows OAuth 设备码、refresh、DPAPI 凭据和只读 profile/usage；它不读取
  浏览器状态或 CLI auth 文件。
- `Core/Runtime` 统一 Startup、Manual、Scheduled、Resume、NetworkRestored 和通知恢复刷新，保证
  单 in-flight、有界退避和失败时保留最后有效状态；额度 provider 由设置选择，切换时先清空旧
  source projection，再加载同 source cache 并立即刷新。
- `Core/Persistence` 保存设置、最小归一化额度缓存、按日聚合 Token 统计缓存和提醒状态；Local Token
  的 SQLite 增量账本由 `Core/TokenUsage` 管理，并存放在同一身份隔离数据目录。
- `Core/Presentation` 是 UI 的唯一产品状态入口；UI 不解析 RPC。
- `Core/TokenUsage` 使用有界 UTF-8 缓冲流式扫描 session 文件中的 Token 计数事件，复用 SQLite 中的
  文件安全偏移增量读取追加内容；累计值按 session high-water、fork replay baseline 计算新增 delta，
  写入持久账本后由 SQL 生成每日聚合。主面板与 LAN 服务共享同一个扫描 single-flight，不进入额度协议层。

完整读取形成通知合并基线。`account/rateLimits/updated` 只覆盖实际出现的字段；无安全基线、通知
溢出或 generation 变化时触发完整补读。

## Android 架构

```text
OAuthStore ───────────────┐
TokenSyncStore ───────────┴─ QuotaSourceRouter ── CodexQuotaRepository
                                           ├─ QuotaSnapshotStore
                                  ├─ QuotaAlertEvaluator
                                  └─ QuotaRefreshWorker

OAuthStore ───────────────┐
TokenSyncStore ───────────┴─ TokenUsageSourceRouter → TokenUsageSyncCoordinator → TokenUsageCache
                                                                          └─ TokenUsageRefreshWorker
```

- `auth` 保存最小 OAuth 状态，Keystore key 不离开 App 私有环境。
- `protocol` 解析 Direct HTTPS usage 响应并保持动态窗口与缺失值语义。
- `quota` 通过独立优先级 Router 双向选择 Direct 或 Windows，并把成功结果提交到同一快照、提醒和通知路径。
- `usage` 通过 process-local single-flight 统一前台、设置和 Worker 同步；提交前重新验证 pairing，
  先写 cache，再写成功时间与最新地址。
- Compose 页面只消费 domain state；前台通过 refresh event 接收后台成功结果。

Token WorkManager 在 OAuth 或 Windows pairing 任一存在时排程，并复用 quota 的 source-aware 网络约束：
仅 OAuth 要求 validated Internet，仅 Windows 要求 Wi-Fi LAN，两者并存时允许 Wi-Fi/cellular 且不硬要求
Internet，以保留 LAN-only Wi-Fi fallback。

额度与 Token Router 都按各自设置串行尝试 OpenAI/Windows；缺少 OAuth 或 pairing 的 provider 视为
unavailable。Windows provider 仍复用已保存地址、必要时 DNS-SD 与 pairing invalidation，LAN 请求绑定
实际 Wi-Fi network。OpenAI Token profile 与 Windows Account/Local 永不合并。

## LAN 服务

Windows 在用户启用后以同一私人 IPv4 listener 提供：

- `GET /v1/token-usage`：聚合 Token 使用量；
- `GET /v1/quota`：Runtime 已有的最后成功归一化额度快照。

Bearer secret 使用固定时间比较。DNS-SD 只发布 `deviceId`、显示名和实际端口；secret 不广播。
- LAN listener 与 DNS-SD publisher 使用同一个 `LanEndpointSelection`：private IPv4 与 interface
  index。地址或 interface index 变化时，controller 重建 listener/publisher。
- monitor 会检测 listener 是否 unhealthy；accept loop 异常或退出后自动重建。
- DNS-SD registration、cancellation 和 deregistration 使用有界生命周期；pending registration
  取消后不依赖 registration callback 作为 terminal signal，deregistration completion 作为 native
  cleanup fence。

Token 普通 LAN 请求使用 stale-while-revalidate；stale cache 立即返回并触发单飞后台刷新，force
请求等待真实 scan。

Android LAN HTTP 请求绑定能够到达 Windows host 的 Wi-Fi network。Token transport 区分
connect-stage failure 与 connection-acquired 后的 read/response failure；只有真正 OFFLINE 的
连接/路由失败才触发 DNS-SD，已建立连接后的 timeout 不进行无意义 discovery。DNS-SD 对候选串行
resolve；错误 deviceId、malformed candidate 或 resolve failure 会继续下一个候选。

Android 只在 offline 类错误时发现相同 `deviceId`，401 不触发发现。

## 身份边界

身份常量以代码为唯一事实源：Windows 见 `AppLaunchProfile`、`TrayIconIdentity` 和
`TokenUsageServiceIdentity`；Android 见 `app/build.gradle.kts`。

- Windows Production、Dev、Preview 使用独立单实例、托盘、数据目录、启动项能力和 LAN identity。
- Release 默认 Production；Debug 默认 Dev；Demo 与 isolated preview 使用 Preview。
- Android Release 与 Debug 使用不同 application ID，因此凭据、配对和缓存自然隔离。
- 一个身份不得删除、覆盖或关闭另一个身份的状态。

来源边界：Quota provider 为 Codex CLI 或 OAuth；Token provider 为 Local、Codex CLI 或 OAuth。
每个 provider 是唯一 source of truth；来源 cache 使用独立 identity，unsupported 或 unavailable
不会静默 fallback。Local 只以本机 JSONL 为输入并由本机 SQLite 账本持久化，账户 usage 只消费按日桶
和可选 summary 字段，两者不合并。

## 持久化和并发边界

- 所有磁盘缓存都是最小产品投影，不是 raw response archive。
- 文件提交使用临时文件/原子替换或平台原子 preferences commit。
- Token cache 保存实际 transport/scope；Windows 结果绑定当前 device identity，OpenAI Account 结果
  绑定 OAuth 可用性，旧缓存迁移为 Windows/Local，同步期间身份改变时丢弃旧结果。
- 同步 single-flight 必须包含完整 pairing 配置的不可逆 fingerprint，不能让换 secret 的请求共享结果。
- 解除配对以删除凭据为强保证，缓存清理仅 best-effort。

## Composition roots

Windows `App.xaml.cs` 组合启动配置、Runtime、窗口、托盘和 LAN 服务。Android Activity/Worker
只创建现有 repository/coordinator，不建立第二套网络或缓存逻辑。
