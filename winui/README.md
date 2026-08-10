# CodexQuotaTray WinUI

`winui/` 是 Windows 正式客户端。产品行为见 [PRD](../docs/PRD.md)，架构见
[TECH_DESIGN](../docs/TECH_DESIGN.md)，依赖版本以项目文件和
[`Directory.Packages.props`](Directory.Packages.props) 为准。

## 本地开发

所有命令从仓库根目录执行。SDK 由 [`global.json`](../global.json) 选择，NuGet 只使用
[`winui/NuGet.Config`](NuGet.Config)。

```powershell
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Quick
dotnet run --project .\winui\src\CodexQuotaTray.App\CodexQuotaTray.App.csproj `
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
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Quick
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Full
```

Quick 与 Full 都构建 Debug/Dev；Full 额外运行格式检查和完整离线测试。`-Mode Release` 只供
GitHub Actions 正式发布路径，不是日常开发命令。三种模式默认都不安装、不签名、不运行真实
账户或 Explorer 托盘 smoke。

正式发布规则集中在 [RELEASE.md](../docs/RELEASE.md)。
