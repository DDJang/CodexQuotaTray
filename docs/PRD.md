# CodexQuotaTray 产品需求

## 产品目标

为个人用户提供低资源占用、只读的 Codex 额度查看体验：Windows 以系统托盘应用为主，Android
以独立 APK 为主。两个客户端不依赖网页抓取，不执行额度或账户写操作。

## 共同需求

- 展示所有可识别额度窗口的剩余百分比、名称、重置时间和数据状态。
- 不把 `primary` / `secondary` 固定解释为特定周期；缺失、未知和 malformed 数据不显示为零。
- 刷新失败保留最后有效数据，并区分刷新中、过期、离线、未登录和不可用。
- 支持手动刷新、可配置后台刷新、主题和脱敏诊断。
- 额度提醒只根据可靠数据触发，同一周期同一阈值不得重复提醒。
- 不读取浏览器 Cookie、网页 DOM、对话正文或项目代码，不记录认证 secret。

## Windows 客户端

- 使用本机 Codex CLI 既有认证，通过 App Server 读取额度。
- 提供主面板、设置、托盘入口、开机启动、缓存和提醒。
- 主面板以额度/统计双页展示额度窗口和本机 Token 日聚合、摘要与热力图。
- 关闭窗口后保持低资源后台运行；显式退出才结束进程。
- Debug/Dev 与 Production 可同时运行，设置、缓存、启动项、托盘和 LAN 身份互不覆盖。
- 可由用户启用私人 LAN 服务，向已配对 Android 提供聚合 Token 使用量和最后成功额度快照。

## Android 客户端

- 使用设备代码 OAuth 登录，凭据保存在 Android Keystore 保护的 App 私有存储。
- 有 OAuth 时 Direct HTTPS usage API 永远是额度主路径；只有 Direct 网络失败、已配对 Windows 且
  Wi-Fi LAN 可用时，才允许读取 Windows 最后成功快照。
- 没有 OAuth 但用户已经配对 Windows 时，Android 可以直接读取 Windows 最后成功额度快照并刷新；
  OAuth 与 Windows pairing 都不存在时才属于没有额度数据源。
- 额度与 Token 使用量各自支持回到前台时自动读取/同步、手动读取和独立周期 WorkManager。
- 回到前台的自动入口由应用级生命周期触发，并以最后一次自动尝试的两分钟窗口抑制重复请求；
  底栏切换只改变页面状态，手动读取不受该窗口限制。
- 支持系统通知权限、额度阈值/重置提醒、主题、日志和电池优化入口。
- Debug 与正式 APK 可同时安装，应用数据互相隔离。

## Token 使用量同步

- Windows 只扫描本机 Codex session 中的 `token_count` 事件时间戳和数字计数，生成日聚合与摘要。
- Windows 可按设置保存最小日聚合统计缓存，用于启动时快速恢复统计页；关闭后删除该缓存。
- Android 必须由用户扫码或手动输入进行配对；LAN 只接受私人 IPv4，不跟随 redirect。
- 配对 secret 与 OAuth 凭据分开加密；Token 缓存绑定 Windows `deviceId`。
- 解除或更换配对后不能显示、提交或恢复旧设备数据。

## 非功能要求

- Windows 支持 Windows 10/11，优先低 idle CPU 和内存占用。
- 网络、进程恢复、DNS-SD 和后台任务必须有界，不无限等待或无限重试。
- 默认测试离线、匿名且可重复；真实账户、通知和系统集成验证必须显式 opt-in。

## 非目标

- Codex 聊天、Agent、项目管理、会话正文同步或远程控制；
- 购买/消费 reset credit、账户修改或任何写 API；
- 多用户服务、云端中转、通用局域网文件访问；
- Electron、嵌入式浏览器或网页抓取。
