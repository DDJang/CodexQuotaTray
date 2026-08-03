# CodexQuotaTray

CodexQuotaTray 是一个轻量、只读的 Windows 系统托盘应用，通过本机 `codex app-server` 显示 Codex 额度窗口、剩余百分比和重置时间。

当前交付候选版本为 `0.5.1`，支持 Windows 10/11，正式入口使用 C# + WinUI 3。旧 Rust/Win32 实现已归档到 `archive/rust-win32-final`，不再保留在当前开发分支。本项目不使用 Electron、WebView 或浏览器运行时。

## 功能边界

- 动态展示 App Server 返回的全部额度窗口，不把 `primary` 或 `secondary` 固定解释为特定周期。
- 展示剩余百分比、重置时间、倒计时以及 fresh、refreshing、stale、offline 等状态。
- 支持自动、5/15/30 分钟和仅手动五种刷新模式；所有触发源由同一个单 in-flight 协调器管理。
- 50%/20%/10% 剩余额度提醒可独立启用（默认关闭 50%，开启 20% 和 10%），跨重启优先防重复。
- 使用 Codex CLI 已有的认证，不读取浏览器 Cookie、Token 文件、网页 DOM、对话或项目代码。
- 当前 App Server schema 不提供权威的重置次数；应用明确显示“暂未提供”，不会从 `credits.balance` 猜测。
- MVP 始终只读，不包含额度重置消费操作。

## 项目结构

| 路径 | 职责 |
|---|---|
| `schemas/` | `codex-cli 0.144.5` 生成的协议基线与版本记录 |
| `assets/` | WinUI 与安装器共用的应用图标 |
| `scripts/` | 图标生成、ZIP/Inno Setup 打包及产物验证 |
| `installer/` | Inno Setup 安装器定义 |
| `docs/` | 产品、技术、协议、发布、隐私与依赖文档 |
| `winui/` | WinUI 3 正式入口、Core 运行时、设置、提醒与离线测试 |

文档入口：

- [产品需求](docs/PRD.md)
- [技术设计](docs/TECH_DESIGN.md)
- [App Server 协议契约](docs/API_CONTRACT.md)
- [路线图](docs/ROADMAP.md)
- [构建与发布](docs/RELEASE.md)
- [隐私说明](docs/PRIVACY.md)
- [依赖与许可证](docs/DEPENDENCIES.md)
- [Rust/Win32 归档说明](docs/LEGACY_RUST_WIN32.md)

`winui/` 是 0.5.1 正式交付候选入口，默认只读连接本机 App Server，并复用 `%LOCALAPPDATA%\CodexQuotaTray` 的兼容设置、缓存与提醒状态。其构建说明见 [WinUI README](winui/README.md)。

## 构建与测试

```powershell
dotnet restore .\winui\CodexQuotaTray.WinUI.sln --configfile .\winui\NuGet.Config
dotnet build .\winui\CodexQuotaTray.WinUI.sln -c Release -p:Platform=x64
dotnet test .\winui\tests\CodexQuotaTray.Tests\CodexQuotaTray.Tests.csproj -c Release -p:Platform=x64
git diff --check
```

测试使用 `winui/tests/fixtures` 下的匿名数据和 fake App Server，不需要真实 Codex 账户。标记为 live smoke 的测试需要显式启用。

## 运行托盘应用

WinUI Release 构建使用本机 Codex CLI：

```powershell
pwsh -NoProfile -File .\scripts\publish-winui.ps1
.\target\winui-publish\codex-quota-tray-gui.exe
```

可用 `--codex-bin PATH` 指定 Codex 可执行文件。安装或卸载时可用 `--shutdown-existing` 请求现有实例正常退出。

卡片聚焦时：`Enter` 刷新，`Tab`/方向键切换按钮，`Space` 执行，`F10` 打开菜单，`Esc` 隐藏。

托盘菜单可调整“额度提醒”和“刷新间隔”。“仅手动”模式只在用户明确点击刷新时主动读取；启动、窗口打开、系统/网络恢复和周期调度均不会发起额度读取，但仍允许安全合并服务端主动推送。

## 打包

安装 Inno Setup 7 后生成 WinUI 自包含的 per-user 安装器：

```powershell
pwsh -NoProfile -File .\scripts\package-inno.ps1
```

需要免安装归档时可独立运行 `scripts\package-winui.ps1`；它会生成递归 SHA-256 清单并排除 PDB。Inno 安装器直接读取 `target\winui-publish`，不依赖该 ZIP。

产物写入被 Git 忽略的 `dist-inno/`，不应提交源码仓库。安装器保留原 AppId、安装路径、升级关闭和 KeepUserData 语义。当前产物未签名，只能标记为 developer build；发布前请阅读 [发布指南](docs/RELEASE.md)。

## 协议升级

当前协议基线是 `codex-cli 0.144.5`。运行时通过实际初始化和 `account/rateLimits/read` 能力检测兼容性；较新的 CLI 版本本身不会触发界面错误。升级协议基线时需要重新生成 schema、更新 `schemas/CODEX_VERSION`、检查 schema diff，并补充匿名 fixture 回归测试：

```powershell
codex app-server generate-json-schema --out schemas/codex-0.144.5
```
