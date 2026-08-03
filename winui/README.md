# CodexQuotaTray WinUI 3

`winui/` 是 CodexQuotaTray 当前的 C# + WinUI 3 正式入口。本文件只说明项目结构、
工具链、启动模式和统一验证入口；产品语义与发布细节分别以根 README 和
`docs/RELEASE.md` 为准。

## 项目结构

| 路径 | 职责 |
| --- | --- |
| `winui/src/CodexQuotaTray.Core` | 协议、运行时、持久化、提醒和展示模型 |
| `winui/src/CodexQuotaTray.App` | WinUI 窗口、XAML、托盘和平台集成 |
| `winui/tests/CodexQuotaTray.Tests` | 完整离线单元与回归测试 |
| `winui/tests/CodexQuotaTray.FakeAppServer` | 可控的离线 App Server 测试进程 |
| `winui/tests/fixtures` | 匿名协议 fixture |

## 工具链

- .NET SDK `10.0.302`，由仓库根目录 `global.json` 固定。
- `rollForward` 为 `latestPatch`，不允许预发布 SDK。
- 测试运行器为 Microsoft Testing Platform。
- Windows SDK 目标为 `10.0.26100`。
- Microsoft Windows App SDK 为 `2.2.0`。
- MSTest.Sdk 为 `4.3.2`。
- NuGet 源固定使用 `winui/NuGet.Config`。

仓库内 SDK 优先于 PATH；验证和发布脚本不会自动下载或安装 SDK。

## 本地运行

所有命令都从仓库根目录执行。先完成 Quick 验证，再启动已构建应用：

```powershell
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Quick
dotnet run --project .\winui\src\CodexQuotaTray.App\CodexQuotaTray.App.csproj `
  -c Release -p:Platform=x64 --no-build
```

Demo 示例：

```powershell
dotnet run --project .\winui\src\CodexQuotaTray.App\CodexQuotaTray.App.csproj `
  -c Release -p:Platform=x64 --no-build -- --demo
```

可用 `--codex-bin <PATH>` 覆盖 Codex CLI 路径；`--startup` 用于 Production
开机启动；`--shutdown-existing` 请求对应身份的已有实例正常退出。

## 启动参数矩阵

| 参数 | 数据源 | 数据位置 | 单实例与托盘身份 | 开机启动设置 |
| --- | --- | --- | --- | --- |
| 无参数 | Live Runtime | Production 数据目录 | Production | 允许 |
| `--demo` | Demo Runtime | 不持久化 | Preview | 不允许 |
| `--isolated-preview-data` | Live Runtime | Preview 数据目录 | Preview | 不允许 |
| `--demo --isolated-preview-data` | Demo Runtime | 不持久化 | Preview | 不允许 |

Production 使用真实 App Server 和正式数据；Demo 使用静态数据；Live Preview 使用
真实 App Server 但隔离数据。Demo 和 Live Preview 可以与 Production 并存，且不会
读取、写入或覆盖 Production 开机启动项。

## 统一验证入口

```powershell
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Quick
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Full
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Release
```

- `Quick`：工具链信息、显式 NuGet restore、Release x64 build、差异检查。
- `Full`：格式校验、Release x64 build、完整离线测试、差异检查。
- `Release`：Full 加自包含 publish 和发布目录检查。

三种模式默认都不会运行真实 Codex smoke 或 Explorer 托盘 smoke。`Release` 也不会
生成 ZIP、编译 Inno、安装或签名。托盘 smoke 仅能通过验证脚本的显式开关调用现有
`scripts/test-winui-tray.ps1`，并要求交互式 Explorer 桌面和关闭所有现有实例。
