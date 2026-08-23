# CodexQuotaTray 隐私说明

CodexQuotaTray 包含本地 Windows 客户端和个人使用 Android APK。两者均无分析埋点、账户后端、
网页抓取、嵌入式浏览器或账户写路径。

## 共同边界

- 不读取浏览器 Cookie、网页 DOM、项目代码或对话内容。
- 不记录 access/refresh token、邮箱、完整账户 ID、设备代码、配对 secret 或原始认证响应。
- 网络/RPC body 只在内存解析，raw response、stderr 和错误正文不持久化。
- 额度功能只读，不购买或消费 reset credit。

## Windows 数据

WinUI 的 Codex CLI 来源通过本机 `codex app-server --stdio` 读取额度与账户 usage，认证由 Codex
CLI 管理；应用不读取 CLI token 文件。OAuth 来源使用设备码登录，access/refresh credential 只以
当前用户 DPAPI 加密形式保存，应用不读取浏览器 Cookie、`auth.json` 或网页内容。OAuth 只读请求
使用 Bearer 与可用的 account ID header；401/403 最多 refresh 一次，失败不会回退到 CLI 或 Local。
Production、Dev 和 Preview 使用相互隔离的数据目录，保存各自设置、按来源隔离的最小额度缓存、
提醒去重状态、Local Token SQLite 账本、可选按日聚合 Token 统计缓存和可选 LAN pairing。

账户页只展示由当前 provider 返回的最小账户字段（如计划或邮箱）；这些字段不写入日志、诊断、
额度缓存或 Token 统计缓存。OAuth profile/usage 只解析 profile、按日桶和 summary 数字，缺失字段
保持不可用，不以业务零值补齐；reset credit 始终只读，从不发起兑换或消耗请求。

本机统计页刷新或启用 Token 使用量同步后，scanner 只遍历 Codex `sessions` 与
`archived_sessions` 中的 JSONL，
过滤 `token_count` 事件，并只消费事件 timestamp 与 `total_token_usage` / `last_token_usage` 的数字
计数。它不会提取、保存、聚合或传输 prompt、response、工具内容、session 正文、项目路径或账户
身份。身份隔离的 SQLite 账本保存 JSONL 路径、session ID、文件 offset、累计 high-water，以及由
timestamp 和数字计数形成的增量事件；不保存其他 session metadata。用于去重的散列仅由 timestamp
与数字计数组成；可选本地缓存和 Android 都只接收日聚合与摘要。关闭“保存统计缓存”后会删除对应
聚合缓存，但不会删除用于防止历史缩水和重复入账的 Local 账本。

Windows LAN 服务只绑定私人 IPv4。DNS-SD 公开稳定随机 deviceId、显示名和端口，不公开 secret。
二维码包含 LAN 地址、deviceId 和独立 pairing secret，不包含 OpenAI 凭据或 Token 数据。应用不
自动提权或修改防火墙。

## Android 数据

- OAuth 凭据与路由 account ID 使用 Android Keystore 保护并存于 App 私有空间。当前仍支持从旧
  App 私有认证文件一次性迁移；成功加密保存后尽力删除旧文件。
- `QuotaSnapshotStore` 保存最后成功的脱敏额度产品快照：套餐、状态、来源、更新时间，以及窗口
  名称/本地标识、百分比、时长和重置时间；历史成功时间仅为缓存兼容和诊断保留。前台自动刷新
  使用进程内最后一次自动尝试时间做抑制。它不保存 OAuth
  token、account ID、HTTP header/body、错误正文或额度历史。
- Token pairing 使用独立 Keystore key。`token-usage-cache.json` 只含 Windows 返回的聚合 schema，
  绑定当前 deviceId；解除或更换 pairing 时清理，不能跨设备显示。
- WorkManager 使用相同 repository/coordinator，不建立额外数据副本。Android LAN 客户端只接受
  RFC1918 IPv4，不跟随 redirect；移动网络不会用于等待 Windows。
- `android:allowBackup` 已关闭。Debug 使用独立 application ID，凭据、配对和缓存不与正式 APK
  共享。卸载对应 APK 会移除其 App 私有数据。

系统浏览器只用于 OAuth 设备授权，应用不嵌入 WebView 或检查浏览器内容。来源切换会清理旧来源
的内存投影，且不复用另一来源缓存。详细字段合同见
[API_CONTRACT](API_CONTRACT.md)。
