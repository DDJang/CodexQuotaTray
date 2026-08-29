# CodexQuotaTray WinUI

`windows/` 是 Windows 正式客户端。产品行为见 [PRD](../docs/PRD.md)，架构见
[TECH_DESIGN](../docs/TECH_DESIGN.md)，依赖版本以项目文件和
[`Directory.Packages.props`](Directory.Packages.props) 为准。

## 本地开发

所有命令从仓库根目录执行。SDK 由 [`global.json`](../global.json) 选择，仓库 restore/package source
语义只由 [`windows/NuGet.Config`](NuGet.Config) 决定；不得依赖或修改用户级 NuGet 配置。

```powershell
pwsh -NoProfile -File .\windows\scripts\verify-winui.ps1 -Mode Quick
$requiredVersion = [string](Get-Content '.\global.json' -Raw | ConvertFrom-Json).sdk.version
$dotnet = @(
  ".\target\dotnet-sdk-$requiredVersion-full\dotnet.exe",
  ".\target\dotnet-sdk-$requiredVersion\dotnet.exe"
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $dotnet) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
& $dotnet run --project .\windows\src\CodexQuotaTray.App\CodexQuotaTray.App.csproj `
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
pwsh -NoProfile -File .\windows\scripts\verify-winui.ps1 -Mode Release
```

Quick 与 Full 都构建 Debug/Dev；Full 额外运行格式检查和完整离线测试。`-Mode Release` 用于本地
release-specific restore/publish 和产物检查，可按仓库 [Validation 规则](../AGENTS.md)验证影响最终
Release 输出的开发改动，但不重复 Full 的格式检查与离线测试。三种模式默认都不安装、不签名、不运行
真实账户或 Explorer 托盘 smoke；本地运行 `-Mode Release` 不等于安装、签名或发布 Production。

正式 Production 产物仍只由 GitHub Actions 从 `main` 上的平台 tag 生成，正式发布入口与边界集中在
[RELEASE.md](../docs/RELEASE.md)。

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
`dotnet test`。Windows 验证和聚焦测试都必须优先解析并显式使用仓库 SDK：

```powershell
$requiredVersion = [string](Get-Content '.\global.json' -Raw | ConvertFrom-Json).sdk.version
$dotnet = @(
  ".\target\dotnet-sdk-$requiredVersion-full\dotnet.exe",
  ".\target\dotnet-sdk-$requiredVersion\dotnet.exe"
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $dotnet) {
  $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
& $dotnet test '.\windows\tests\CodexQuotaTray.Tests\CodexQuotaTray.Tests.csproj' `
  -c Debug -p:RestoreConfigFile=$config --no-restore
```

`windows/NuGet.Config` 使用 `<clear />`，这决定仓库 restore/package source，但不保证每个
MSBuild/NuGet SDK resolver 阶段都不会触碰用户级 `%APPDATA%\NuGet\NuGet.Config`。如果当前
sandbox 在读取 SDK、NuGet cache 或用户级配置时出现明确 `AccessDenied`、`UnauthorizedAccessException`
或 permission denied，应将其分类为环境权限问题，并对同一仓库验证命令申请一次 elevated rerun。
提升权限只用于执行验证，不得安装 SDK、编辑用户配置、修改 ACL、删除用户配置或创建替代
`global.json`；elevated 后仍失败才停止并报告原始错误与提升权限后的错误。
