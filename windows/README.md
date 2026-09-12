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

以下是按改动范围选择的独立入口，不是依次执行的步骤；最终验证组合见 [Validation 规则](../AGENTS.md)。

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

如需单独运行测试，先按验证脚本的顺序选择并检查 SDK，再用同一 SDK、solution 和仓库配置完成
restore；restore 成功后才运行带 `--no-restore` 的测试。以下命令从仓库根目录执行：

```powershell
$solution = '.\windows\CodexQuotaTray.WinUI.sln'
$config = '.\windows\NuGet.Config'
$ErrorActionPreference = 'Stop'

$requiredVersion = [string](Get-Content '.\global.json' -Raw | ConvertFrom-Json).sdk.version
$dotnet = @(
  ".\target\dotnet-sdk-$requiredVersion-full\dotnet.exe",
  ".\target\dotnet-sdk-$requiredVersion\dotnet.exe"
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $dotnet) {
  $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
$actualVersion = & $dotnet --version
if ($LASTEXITCODE -ne 0) { throw '无法读取所选 SDK 版本。' }
$actual = [version]$actualVersion
$required = [version]$requiredVersion
if ($actual.Major -ne $required.Major -or $actual.Minor -ne $required.Minor -or
    [Math]::Floor($actual.Build / 100) -ne [Math]::Floor($required.Build / 100) -or
    $actual.Build -lt $required.Build) {
  throw "所选 SDK $actualVersion 不符合 global.json 的 latestPatch 规则。"
}
& $dotnet restore $solution --configfile $config -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'Restore 失败，停止测试。' }
& $dotnet test '.\windows\tests\CodexQuotaTray.Tests\CodexQuotaTray.Tests.csproj' `
  -c Debug -p:RestoreConfigFile=$config --no-restore
if ($LASTEXITCODE -ne 0) { throw '聚焦测试失败。' }
```

不要使用不存在的 `windows\CodexQuotaTray.sln`，也不要在未成功 restore 前直接运行未指定配置的
`dotnet test`。SDK 解析与版本兼容策略以 `verify-winui.ps1` 为准，不以安装或降级 SDK 绕过失败。

`windows/NuGet.Config` 使用 `<clear />`，这决定仓库 restore/package source，但不保证每个
MSBuild/NuGet SDK resolver 阶段都不会触碰用户级 `%APPDATA%\NuGet\NuGet.Config`。如果当前
sandbox 在读取 SDK、NuGet cache 或用户级配置时出现明确 `AccessDenied`、`UnauthorizedAccessException`
或 permission denied，应将其分类为环境权限问题；仅在当前执行环境支持且允许申请提权时，
对同一仓库验证命令申请一次 elevated rerun。环境不支持或禁止申请时，直接报告原始错误与环境阻塞。
提升权限只用于执行验证，不得安装 SDK、编辑用户配置、修改 ACL、删除用户配置或创建替代
`global.json`；elevated 后仍失败则停止并报告原始错误与提升权限后的错误。
