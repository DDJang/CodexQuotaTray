# CodexQuotaTray

CodexQuotaTray 是一个轻量、只读的 Windows 系统托盘应用，通过本机
`codex app-server` 显示 Codex 额度窗口、剩余百分比和重置时间。

当前正式入口是 `winui/` 下的 C# + WinUI 3 应用。迁移前最后一套 Rust/Win32
实现保存在 Git tag `archive/rust-win32-final`，不参与当前构建、测试和发布；旧版
细节需要时直接通过 Git 历史查看。本项目不使用 Electron、WebView 或浏览器运行时。

仓库同时包含个人使用的实验性 Android 独立 APK。历史 P0–P3 已验证 ARM64 runtime、
App Server、App 内登录、真实额度、手动刷新、产品 UI、恢复和打包；当前日常 Android
路径已收敛为 App 私有 OAuth + Direct HTTPS usage，不再依赖 embedded runtime 或
App Server。后续范围仅为后台自动刷新、通知、Widget 和开机启动；具体见
[Android Roadmap](docs/ANDROID_ROADMAP.md)。

## 主要功能

- 动态展示 App Server 返回的额度窗口，不把 `primary`、`secondary` 固定解释为特定周期。
- 显示剩余百分比、重置时间、倒计时和刷新状态。
- 支持自动刷新、手动刷新、额度提醒、主题、缓存和开机启动设置。
- 托盘菜单保持精简：打开面板、设置、退出。
- 使用 Codex CLI 已有认证，不读取浏览器 Cookie、网页 DOM、对话或项目代码。

## 只读和隐私边界

- 应用只读取额度信息，不执行额度消费、重置卡消费或账户写操作。
- WinUI 不在日志或明文配置中保存访问令牌、刷新令牌或浏览器 Cookie；Android 使用
  Android Keystore 保护 App 私有 OAuth Store。
- 不把未知字段或缺失数据静默解释为零。
- 离线测试使用匿名 fixture 和 Fake App Server，不需要真实 Codex 账户。

## 快速运行

在仓库根目录执行：

```powershell
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Quick
dotnet run --project .\winui\src\CodexQuotaTray.App\CodexQuotaTray.App.csproj `
  -c Debug -p:Platform=x64 --no-build
```

日常本地开发只运行 Debug 的 **CodexQuotaTray Dev**；它与已安装的正式版使用独立实例、
托盘、数据、启动项和 LAN identity。不要在日常开发中生成或安装正式 Release。正式 Windows
Release 只能在 `main` 提交上的 `windows-v*` tag 触发 GitHub Actions 构建和发布。可用
`--codex-bin <PATH>` 指定 Codex CLI；开发演示和隔离预览参数见
[WinUI 开发说明](winui/README.md)。

## 统一验证入口

```powershell
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Quick
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Full
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Release
```

`Quick` 与 `Full` 使用 Debug/Dev build；`Release` 才使用 Production Release build 并生成
发布目录。日常开发只使用前两者；正式发布只由 GitHub Actions 处理。三者默认都不生成 ZIP
或安装器，不安装、签名，也不运行真实账户或 Explorer 托盘 smoke。打包和发布操作以发布文档为准。

## 文档导航

- [WinUI 项目、启动模式和本地运行](winui/README.md)
- [产品需求](docs/PRD.md)
- [技术设计](docs/TECH_DESIGN.md)
- [App Server 协议契约](docs/API_CONTRACT.md)
- [Windows 路线图](docs/ROADMAP.md)
- [Android 路线图（个人实验性路线）](docs/ANDROID_ROADMAP.md)
- [构建与发布](docs/RELEASE.md)
- [隐私说明](docs/PRIVACY.md)
- [依赖与许可证](docs/DEPENDENCIES.md)
