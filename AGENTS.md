# CodexQuotaTray Development Instructions

## Scope

CodexQuotaTray 有两个独立、只读的客户端：

- `windows/`：Windows 10/11 的 C# + WinUI 3 正式客户端；
- `android/`：个人使用的 Kotlin + Jetpack Compose APK；额度与 Token 分别在 OpenAI（OAuth + Direct
  HTTPS）和已配对 Windows LAN 之间按独立优先级选择。

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
- `quota`：额度仓库、快照、提醒提交、独立 OpenAI/Windows Router、WorkManager 与 LAN fallback；
- `usage`：独立 Token Router、Windows 配对、LAN 同步、缓存和后台 Worker；
- Activity / Compose 文件：产品投影，不解析原始网络响应；
- `app/src/test`：匿名、离线回归测试。

UI 不得直接解析 raw JSON/RPC。前后台读取必须复用同一数据提交路径，不能维护相互冲突的缓存。

## Domain rules

- 字段默认可缺失，除非权威 schema 明确 required；未知或 malformed 不得变成零。
- 不把 `primary` / `secondary` 固定解释为五小时或七天；按时长、名称和标识识别窗口。
- 刷新失败保留最后有效状态，并区分 refreshing、stale、offline、unauthenticated 与 unavailable。
- WinUI reset-credit 数量只接受 `availableCount`。
- Android 的额度与 Token 来源优先级彼此独立；默认额度 OpenAI 优先、Token Windows 优先。各自 Router
  按设置顺序尝试 OpenAI 与 Windows，首选来源失败或不可用时尝试另一来源；缺少 OAuth 或 Windows
  pairing 的 provider 视为 unavailable。
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

## Maintainer-local Android environment

仓库权威配置以 `global.json`、Gradle Wrapper / `build.gradle.kts`、[`windows/README.md`](windows/README.md)
和 [`android/README.md`](android/README.md) 为准。本节只记录当前 maintainer Windows 开发机的
fallback 事实，不是普通开发者的安装路径，也不是长期稳定合同：

- PATH 当前可能命中 `C:\Windows\System32\java.exe`（Java 1.7），不得用于 Android 构建；已验证可运行
  仓库 Gradle 8.11.1 / AGP 8.9.1 的 Gradle JVM 是 `C:\Users\18456\.jdks\jbr-21.0.11`。
- 已验证 Android SDK：`D:\Android\Sdk`。Android Studio 自带的 `D:\Android\Android Studio\jbr`
  当前为 JDK 25，不作为本仓库 Gradle 验证 JVM。

使用上述 fallback 前必须先用 `Test-Path`、`java -version` 和 `gradlew --version` 验证；任一路径不存在或
版本不匹配，都必须停止并报告“maintainer 本机环境记录已过期”。Gradle 运行 JVM 可使用已验证的 JBR 21，
但项目 Java/Kotlin 编译 target 仍以仓库 `build.gradle.kts` 配置为准（当前为 Java 17），不得因此修改 target。
不得因此安装 SDK/JDK、修改 PATH、Gradle、AGP、target 或项目配置（包括 `gradle.properties`），也不得猜测新的机器路径；
`JAVA_HOME`、`ANDROID_HOME` 和 `ANDROID_SDK_ROOT` 只应作用于当前 shell 进程。

## Validation

WinUI 从仓库根目录运行 `windows/scripts/verify-winui.ps1`：

- `Quick`：仓库配置 restore、Debug/Dev x64 build、基础检查；
- `Full`：Quick 加格式和完整离线测试，仍使用 Debug/Dev；
- `Release`：Production Release build 与 publish 检查，只供正式发布流程。

Core、协议、持久化和 runtime 改动至少运行 Full。真实账户和 Explorer 托盘 smoke 始终显式 opt-in。

Android 使用仓库 Gradle Wrapper、兼容的 Gradle JVM 和 Android SDK 35；项目 Java/Kotlin 编译 target
仍为 Java 17：

- Kotlin、协议、UI 或持久化：`:app:testDebugUnitTest` 与 `:app:assembleDebug`；
- 文档：链接/引用检查与 `git diff --check`；
- 本地开发不得读取 Release JKS、密码、alias 或签名配置。

## Privacy

不得记录 access/refresh token、Cookie、邮箱、完整账户标识、完整 reset-credit ID、配对 secret、
原始认证响应或对话正文。日志和 fixture 必须匿名、最小且离线。

## GitHub 发布流程

只有用户明确指定平台和版本时，才视为进入 GitHub 发布流程，例如：

“进入 GitHub 发布流程，平台 Windows，版本号 X.Y.Z”
“进入 GitHub 发布流程，平台 Android，版本号 X.Y.Z”
“进入 GitHub 发布流程，平台 All，版本号 X.Y.Z”

Windows/Android 单平台发布只授权对应平台的 version、release notes、commit、push、PR、CI、tag、
Release workflow、Release 和 `update-manifest` manifest 节点；只有 `All` 才授权两套平台同步发布。
执行细节以 [RELEASE_PROCESS](docs/RELEASE_PROCESS.md) 为准。

发布流程仍必须遵守：

- 发布前先阅读 `docs/RELEASE_PROCESS.md`；
- 选定平台的 PR CI 失败、取消或错误时立即停止，不得 merge；merge 后确认目标 commit 已进入
  `origin/main`，普通 `main` push 不作为额外发布门禁；
- 不 force push、不移动或删除已有 tag、不跳过 CI、不修改测试来绕过失败；
- 选定平台的 release notes 比较该平台上一 Release tag 到待发布 HEAD 的用户可感知变化，并在调用脚本前提交到当前 HEAD；调用脚本时工作区必须 clean；
- Windows/Android 单平台只要求对应 Release workflow 和 manifest 节点成功；只有 `All` 才要求两套 Release workflow 和两个 manifest 节点都成功；
- 若出现需要 Owner 决策的真实冲突或不可安全自动化的情况，停止并明确报告。
