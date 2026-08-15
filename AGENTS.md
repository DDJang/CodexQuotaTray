# CodexQuotaTray Development Instructions

## Scope

CodexQuotaTray 有两个独立、只读的客户端：

- `windows/`：Windows 10/11 的 C# + WinUI 3 正式客户端；
- `android/`：个人使用的 Kotlin + Jetpack Compose APK，额度主路径为 OAuth + Direct HTTPS。

除非任务明确要求跨平台修改，Android 工作不得改变 WinUI 行为，WinUI 工作不得改变 Android
行为。旧 Rust/Win32 与 Android runtime/Bridge 实验只保留在 Git history，不参与当前仓库构建。

不得使用 Electron、嵌入浏览器、网页抓取、浏览器 Cookie 或账户写接口。两个客户端都不得消费
reset credit。

## Documentation authority

只读取与任务有关的权威文档：

- 产品行为：[PRD](docs/PRD.md)
- 架构与身份：[TECH_DESIGN](docs/TECH_DESIGN.md)
- 协议与持久化合同：[API_CONTRACT](docs/API_CONTRACT.md)
- 发布：[RELEASE](docs/RELEASE.md)
- 发布执行状态机：[RELEASE_PROCESS](docs/RELEASE_PROCESS.md)
- 隐私：[PRIVACY](docs/PRIVACY.md)
- Windows / Android 本地命令：各自 README
- 平台后续方向：对应 Roadmap

不要在多个文档重复维护版本、端口、依赖版本或发布步骤；优先链接到权威来源。

## Task lifecycle

任务开始时报告仓库根目录、分支、HEAD、包含未跟踪文件的工作区状态和验证级别。修改前先读
实现与测试，保留用户已有改动，只做满足任务的最小一致变更。

任务结束时只报告本轮改动、验证、跳过的 opt-in 检查、剩余风险和最终工作区状态。

## Architecture boundaries

WinUI：

- `Core/Protocol`：App Server 进程、JSONL transport、DTO 与规范化；
- `Core/Runtime`：连接、刷新协调和当前状态；
- `Core/Persistence`：设置、缓存和提醒去重；
- `Core/Presentation`：UI 投影；
- `Core/Alerts`：提醒状态机；
- `App/Views`、`Services`、`Interop`、`Themes`：WinUI 与平台集成；
- `Tests`、`FakeAppServer`：确定性离线验证。

Android：

- `auth`：OAuth 与 Keystore 持久化；
- `protocol`：Direct HTTPS DTO 与解析；
- `quota`：额度仓库、快照、提醒提交、WorkManager 与 Windows fallback；
- `usage`：Windows 配对、LAN 同步、缓存和后台 Worker；
- Activity / Compose 文件：产品投影，不解析原始网络响应；
- `app/src/test`：匿名、离线回归测试。

UI 不得直接解析 raw JSON/RPC。前后台读取必须复用同一数据提交路径，不能维护相互冲突的缓存。

## Domain rules

- 字段默认可缺失，除非权威 schema 明确 required；未知或 malformed 不得变成零。
- 不把 `primary` / `secondary` 固定解释为五小时或七天；按时长、名称和标识识别窗口。
- 刷新失败保留最后有效状态，并区分 refreshing、stale、offline、unauthenticated 与 unavailable。
- WinUI reset-credit 数量只接受 `availableCount`。
- Android 有 OAuth 时 Direct HTTPS 永远优先，只有网络失败才允许 Windows LAN fallback；无 OAuth 但有
  Windows pairing 时可以直接读取 Windows-only quota。
- 重试、超时、进程恢复和局域网发现必须有界。

## Build identities

- Windows Release/Production 保持既有实例、托盘、数据、启动项和安装器身份；Debug 使用独立 Dev
  身份；Demo/isolated preview 使用 Preview 身份。
- Android Release 保持正式 application ID；Debug 使用 `.debug` 后缀和独立应用数据。
- 日常开发只构建/运行 Dev 或 Debug，不安装或签名正式版。
- 正式 Release 只由 GitHub Actions 从 `main` 上的平台 tag 构建。

未经明确授权，不修改产品版本、协议基线、依赖版本、target framework/SDK、Production identity、
tray GUID 或 Installer AppId。

## Working-tree and environment safety

- 不使用 `git reset`、`clean`、`restore`、`stash` 覆盖用户工作；不碰无关改动。
- 未经授权不 commit、push、创建 PR/tag/Release、安装应用、打包、签名或运行真实账户 smoke。
- 本地构建输出被占用时，可直接关闭 CodexQuotaTray Debug/Dev 进程，无需再次询问；不得在未获
  明确授权时关闭 Production/正式版进程。
- 不安装 SDK，不修改用户 NuGet/Gradle 配置，不创建替代 `global.json`，不降级依赖解决环境问题。
- 第一次失败先区分代码与环境。Windows 验证必须优先按 `verify-winui.ps1` 的顺序使用
  `target\\dotnet-sdk-<global.json version>-full\\dotnet.exe`、`target\\dotnet-sdk-<global.json version>\\dotnet.exe`，
  最后才使用 PATH 中符合 `global.json` 的 SDK；聚焦 `dotnet` 命令也必须显式使用同一解析结果。
- 如果 PATH SDK 不匹配，先使用仓库已有 SDK/配置重试一次；如果随后出现明确的 sandbox
  `AccessDenied`、`UnauthorizedAccessException` 或用户级 NuGet 配置读取权限错误，允许对同一验证命令申请一次
  elevated execution。这是规定的环境恢复步骤，不属于第三种绕过方式。elevated 后仍失败才停止并报告。
- WinUI 的 NuGet 恢复、聚焦测试和权限处理遵循 [`windows/README.md`](windows/README.md) 的“NuGet 与环境恢复”小节；不要绕过仓库配置直接使用用户级 NuGet 配置。

## Validation

WinUI 从仓库根目录运行 `windows/scripts/verify-winui.ps1`：

- `Quick`：仓库配置 restore、Debug/Dev x64 build、基础检查；
- `Full`：Quick 加格式和完整离线测试，仍使用 Debug/Dev；
- `Release`：Production Release build 与 publish 检查，只供正式发布流程。

Core、协议、持久化和 runtime 改动至少运行 Full。真实账户和 Explorer 托盘 smoke 始终显式 opt-in。

Android 使用仓库 Gradle Wrapper、JDK 17 和 Android SDK 35：

- Kotlin、协议、UI 或持久化：`:app:testDebugUnitTest` 与 `:app:assembleDebug`；
- 文档：链接/引用检查与 `git diff --check`；
- 本地开发不得读取 Release JKS、密码、alias 或签名配置。

## Privacy

不得记录 access/refresh token、Cookie、邮箱、完整账户标识、完整 reset-credit ID、配对 secret、
原始认证响应或对话正文。日志和 fixture 必须匿名、最小且离线。

## GitHub 发布流程

当用户明确说：

“进入 GitHub 发布流程，版本号 X.Y.Z”

视为已授权本次发布所需的版本修改、release notes 生成、commit、push、PR 创建或复用、等待 CI、
merge 到 `main`、创建并 push Android/Windows tags、监控 Release workflow，以及验证 GitHub Release
和 `update-manifest`。执行细节以 [RELEASE_PROCESS](docs/RELEASE_PROCESS.md) 为准。

发布流程仍必须遵守：

- 发布前先阅读 `docs/RELEASE_PROCESS.md`；
- 任一 PR 或 `main` CI 失败立即停止，`main` CI 全部通过前不得 push release tag；
- 不 force push、不移动或删除已有 tag、不跳过 CI、不修改测试来绕过失败；
- Android 与 Windows 的 release notes 分别比较上一平台 Release tag 到待发布 HEAD 的用户可感知变化；
- 只有两个 Release workflow 和 `update-manifest` 都成功后，才能报告发布成功；
- 若出现需要 Owner 决策的真实冲突或不可安全自动化的情况，停止并明确报告。
