# CodexQuotaTray 技术设计

本文只记录当前架构和兼容边界。产品行为见 [PRD](PRD.md)，wire 与持久化字段见
[API_CONTRACT](API_CONTRACT.md)，发布流程见 [RELEASE](RELEASE.md)。

## Windows 架构

```text
codex app-server --stdio
        ↓ UTF-8 JSONL
Core/Protocol
        ↓ normalized quota
Core/Runtime ── Core/Persistence ── Core/Alerts
        ↓
Core/Presentation
        ↓
WinUI Views / Services / Interop / Themes
```

- `Core/Protocol` 管理 CLI 定位、子进程、JSONL transport、DTO、通知和规范化。
- `Core/Runtime` 统一 Startup、Manual、Scheduled、Resume、NetworkRestored 和通知恢复刷新，保证
  单 in-flight、有界退避和失败时保留最后有效状态。
- `Core/Persistence` 只保存设置、最小归一化额度缓存和提醒状态。
- `Core/Presentation` 是 UI 的唯一产品状态入口；UI 不解析 RPC。
- `Core/TokenUsage` 流式扫描 session 文件中的 Token 计数事件并生成聚合，不进入额度协议层。

完整读取形成通知合并基线。`account/rateLimits/updated` 只覆盖实际出现的字段；无安全基线、通知
溢出或 generation 变化时触发完整补读。

## Android 架构

```text
OAuthStore → DirectQuotaClient → CodexQuotaRepository
                                  ├─ QuotaSnapshotStore
                                  ├─ QuotaAlertEvaluator
                                  └─ QuotaRefreshWorker

TokenSyncStore → TokenUsageSyncCoordinator → TokenUsageCache
                                           └─ TokenUsageRefreshWorker
```

- `auth` 保存最小 OAuth 状态，Keystore key 不离开 App 私有环境。
- `protocol` 解析 Direct HTTPS usage 响应并保持动态窗口与缺失值语义。
- `quota` 把 Direct 或 Windows fallback 的成功结果提交到同一快照、提醒和通知路径。
- `usage` 通过 process-local single-flight 统一前台、设置和 Worker 同步；提交前重新验证 pairing，
  先写 cache，再写成功时间与最新地址。
- Compose 页面只消费 domain state；前台通过 refresh event 接收后台成功结果。

额度 Direct 请求与 Windows LAN fallback 保持串行：只有 Direct 的 `NETWORK` 失败才尝试已保存
地址、必要时 DNS-SD、再重试 Windows。LAN 请求绑定实际 Wi-Fi network，不在移动网络等待。

## LAN 服务

Windows 在用户启用后以同一私人 IPv4 listener 提供：

- `GET /v1/token-usage`：聚合 Token 使用量；
- `GET /v1/quota`：Runtime 已有的最后成功归一化额度快照。

Bearer secret 使用固定时间比较。DNS-SD 只发布 `deviceId`、显示名和实际端口；secret 不广播。
地址变化以同一 `deviceId` 重启 listener/registration。Android 只在 offline 类错误时发现相同
`deviceId`，401 不触发发现。

## 身份边界

身份常量以代码为唯一事实源：Windows 见 `AppLaunchProfile`、`TrayIconIdentity` 和
`TokenUsageServiceIdentity`；Android 见 `app/build.gradle.kts`。

- Windows Production、Dev、Preview 使用独立单实例、托盘、数据目录、启动项能力和 LAN identity。
- Release 默认 Production；Debug 默认 Dev；Demo 与 isolated preview 使用 Preview。
- Android Release 与 Debug 使用不同 application ID，因此凭据、配对和缓存自然隔离。
- 一个身份不得删除、覆盖或关闭另一个身份的状态。

## 持久化和并发边界

- 所有磁盘缓存都是最小产品投影，不是 raw response archive。
- 文件提交使用临时文件/原子替换或平台原子 preferences commit。
- Token cache 绑定当前 Windows device identity；同步期间配对改变时丢弃旧结果。
- 同步 single-flight 必须包含完整 pairing 配置的不可逆 fingerprint，不能让换 secret 的请求共享结果。
- 解除配对以删除凭据为强保证，缓存清理仅 best-effort。

## Composition roots

Windows `App.xaml.cs` 组合启动配置、Runtime、窗口、托盘和 LAN 服务。Android Activity/Worker
只创建现有 repository/coordinator，不建立第二套网络或缓存逻辑。
