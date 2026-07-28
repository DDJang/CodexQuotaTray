# CodexQuotaTray WinUI 3

这是 CodexQuotaTray 0.3.5 的 WinUI 3 正式交付候选。默认模式连接本机 Codex App Server 并读取真实额度；`--demo` 只用于开发演示。

0.3.5 使用 message-only HWND 承载托盘回调，另一个隐藏 tool window 接收 Explorer 重建和系统恢复广播。Shell P/Invoke 显式绑定 `Shell_NotifyIconW`/`Shell_NotifyIconGetRect`，只有 Explorer 返回非空图标矩形后才视为注册成功。窗口外轮廓只由 DWM 圆角裁剪，XAML 不再叠加第二套圆角；窗口置顶并可从标题区自由拖动，本次运行内保留最后位置。自动刷新与手动刷新共用同一按钮忙碌状态。视觉继续使用蓝色 Desktop Acrylic，并在 Mica/不透明环境安全降级。

## 安全边界

- 默认数据源为单一 `QuotaRuntimeService`，只发送 `initialize`、`initialized` 和 `account/rateLimits/read`，并被动接收 `account/rateLimits/updated`。
- 不调用 consume/write 方法；默认使用兼容的 `%LOCALAPPDATA%\CodexQuotaTray` 数据目录。
- 正式入口沿用原 AppId、进程名、单实例身份、托盘 GUID 和启动项名称，支持覆盖升级 Rust 版。
- `--isolated-preview-data` 可在 smoke 时切换到隔离目录，不触碰正式用户数据。

## 工具链

- .NET SDK `10.0.302`（由 `global.json` 固定）；
- Windows SDK `10.0.26100`；
- x64 Windows 10 19041 或更高版本；
- Microsoft Windows App SDK `2.2.0`。

依赖版本集中在 `Directory.Packages.props`。本阶段额外使用 CommunityToolkit.Mvvm `8.4.2` 和 MSTest `4.3.2`，均为 MIT 许可。

## 构建与运行

```powershell
dotnet restore CodexQuotaTray.WinUI.sln --configfile NuGet.Config
dotnet format CodexQuotaTray.WinUI.sln --verify-no-changes --no-restore
dotnet build CodexQuotaTray.WinUI.sln -c Release -p:Platform=x64 --no-restore
dotnet test CodexQuotaTray.WinUI.sln -c Release --no-build
```

真实通知区域 smoke：

```powershell
.\scripts\test-winui-tray.ps1 -Executable <published-exe> -Cycles 100
```

普通启动后创建独立托盘并按刷新模式工作；传入 `--demo` 才使用静态数据：

```powershell
dotnet run --project src/CodexQuotaTray.App -c Release --no-build -- --demo
```

可用 `--codex-bin <PATH>` 覆盖 CLI。应用优先发现 `%APPDATA%\npm\codex.cmd`；Microsoft Store 版 CLI 当前不允许 unpackaged 子进程重定向 stdio 时，需安装官方 npm 版 Codex CLI。`--startup` 遵守静默启动设置。

设置窗口支持 Auto/5/15/30 分钟/ManualOnly、50%/20%/10% 提醒、显示与时间偏好、缓存、网络恢复、主题和预览版开机启动。ManualOnly 仍连接并接收安全的主动推送，但只允许用户手动发起读取。

窗口关闭只会隐藏。使用托盘右键菜单中的“退出”才会结束进程；`--shutdown-existing` 用于安装升级和卸载。

## 项目边界

- `CodexQuotaTray.Core`：JSONL dispatcher、受控 App Server、DTO、额度归一化、刷新协调器、提醒 reducer、兼容持久化、展示投影和 ViewModel，不依赖 WinUI。
- `CodexQuotaTray.App`：WinUI composition root、XAML、系统 backdrop、剪贴板诊断和原生托盘桥接。
- `CodexQuotaTray.FakeAppServer` / `CodexQuotaTray.Tests`：完全离线的协议与 UI 回归测试。

正式自包含发布由 `scripts/publish-winui.ps1` 生成，Inno 安装器由 `scripts/package-inno.ps1` 生成。
