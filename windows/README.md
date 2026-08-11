# CodexQuotaTray WinUI

`windows/` 是 Windows 正式客户端。产品行为见 [PRD](../docs/PRD.md)，架构见
[TECH_DESIGN](../docs/TECH_DESIGN.md)，依赖版本以项目文件和
[`Directory.Packages.props`](Directory.Packages.props) 为准。

## 本地开发

所有命令从仓库根目录执行。SDK 由 [`global.json`](../global.json) 选择，NuGet 只使用
[`windows/NuGet.Config`](NuGet.Config)。

```powershell
pwsh -NoProfile -File .\windows\scripts\verify-winui.ps1 -Mode Quick
dotnet run --project .\windows\src\CodexQuotaTray.App\CodexQuotaTray.App.csproj `
  -c Debug -p:Platform=x64 --no-build
```

Debug 默认启动 **CodexQuotaTray Dev**。静态 Demo 可追加 `--demo`，真实数据隔离预览可追加
`--isolated-preview-data`；`--codex-bin <PATH>` 可指定 Codex CLI。

| 构建 / 参数 | 数据 | 身份 | 启动项 |
| --- | --- | --- | --- |
| Release | Production | Production | 允许 |
| Debug | Dev | Dev | 独立允许 |
| `--demo` | 静态、不持久化 | Preview | 禁止 |
| `--isolated-preview-data` | Live Preview | Preview | 禁止 |

Production、Dev、Preview 使用独立单实例 key、托盘 GUID、数据目录和 LAN listener identity，
可以并存。具体身份来源见 [技术设计](../docs/TECH_DESIGN.md)。

## 验证

```powershell
pwsh -NoProfile -File .\windows\scripts\verify-winui.ps1 -Mode Quick
pwsh -NoProfile -File .\windows\scripts\verify-winui.ps1 -Mode Full
```

Quick 与 Full 都构建 Debug/Dev；Full 额外运行格式检查和完整离线测试。`-Mode Release` 只供
GitHub Actions 正式发布路径，不是日常开发命令。三种模式默认都不安装、不签名、不运行真实
账户或 Explorer 托盘 smoke。

正式发布规则集中在 [RELEASE.md](../docs/RELEASE.md)。

## NuGet 与环境恢复

WinUI 的唯一 solution 路径是 [`CodexQuotaTray.WinUI.sln`](CodexQuotaTray.WinUI.sln)，仓库专用
NuGet 配置是 [`NuGet.Config`](NuGet.Config)。日常验证优先使用
[`windows/scripts/verify-winui.ps1`](scripts/verify-winui.ps1)，它会在 restore 时传入
`--configfile .\windows\NuGet.Config`，并在后续 build/测试阶段传入
`-p:RestoreConfigFile=.\windows\NuGet.Config`。

如需单独运行测试，必须先用同一 solution 和仓库配置完成 restore，再使用 `--no-restore`：

```powershell
$solution = '.\windows\CodexQuotaTray.WinUI.sln'
$config = '.\windows\NuGet.Config'

dotnet restore $solution --configfile $config -p:Platform=x64
dotnet test '.\windows\tests\CodexQuotaTray.Tests\CodexQuotaTray.Tests.csproj' `
  -c Debug -p:RestoreConfigFile=$config --no-restore
```

不要使用不存在的 `windows\CodexQuotaTray.sln`，也不要在未成功 restore 前直接运行未指定配置的
`dotnet test`。`windows/NuGet.Config` 使用 `<clear />`，因此不会依赖用户级
`%APPDATA%\NuGet\NuGet.Config`；不要删除、修改或放宽该用户配置的权限。

如果沙箱拒绝读取 SDK/NuGet 缓存，应请求一次提升权限后重跑上述仓库命令；提升权限只用于执行
验证，不得借机安装 SDK、编辑用户配置或修改系统 ACL。首次环境失败最多做一次基于仓库配置的
修正重试，之后停止并报告原始错误与修正错误。
