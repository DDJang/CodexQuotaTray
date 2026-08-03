# Release and packaging guide

## 权威来源

- 产品版本只从 [App 项目文件](../winui/src/CodexQuotaTray.App/CodexQuotaTray.App.csproj)读取；本文不维护版本副本。
- SDK 和默认测试运行器由仓库根目录 [`global.json`](../global.json) 定义。
- 普通验证行为以 [`scripts/verify-winui.ps1`](../scripts/verify-winui.ps1) 为准。
- Publish、便携 ZIP 和 Inno 行为分别以对应 PowerShell 脚本和安装器定义为准。

所有命令从仓库根目录执行。发布操作必须在干净、已审阅的目标提交上进行；不要把 `target/`、`dist/`、`dist-inno/`、`bin/`、`obj/` 或 `TestResults/` 提交到仓库。

## 默认验证

普通开发验证使用：

```powershell
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Quick
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Full
```

正式产物准备使用：

```powershell
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Release
```

Release 模式执行 Full 验证，然后调用现有 publish 脚本并检查发布目录中的应用 EXE、应用图标和运行时文件。它默认不生成 ZIP、不编译 Inno、不安装应用、不签名，也不运行 Explorer 托盘或真实账户 smoke。

真实账户测试在离线 Full 中固定保持 opt-in 跳过。任何启用都必须是单独授权的只读 smoke，并禁止保存原始账户或额度响应。

## Publish

需要单独生成 folder-based、self-contained x64 发布目录时执行：

```powershell
pwsh -NoProfile -File .\scripts\publish-winui.ps1
```

默认输出目录为 `target/winui-publish/`。脚本从根 `global.json` 解析 SDK 要求，优先使用仓库 SDK，再验证 PATH dotnet；restore 显式使用仓库 NuGet 配置。

发布目录至少应包含：

- `codex-quota-tray-gui.exe`；
- `Assets/AppIcon.ico`；
- 应用和 Windows App SDK 所需运行时文件。

Publish 不是 ZIP、安装器、安装或签名操作。

## 便携 ZIP

便携归档由现有脚本显式生成：

```powershell
pwsh -NoProfile -File .\scripts\package-winui.ps1
```

脚本默认先调用 publish，再将完整发布目录复制到受控 staging 目录，排除 PDB，并加入项目说明、隐私说明和依赖说明。产品版本从 App 项目文件读取，用于归档名称和 `VERSION` 文件。

ZIP 包含递归 `MANIFEST.sha256`。Manifest 用于发现意外损坏，不是签名或真实性边界；能同时替换文件和 manifest 的攻击者仍可伪造结果。

便携 ZIP 与 Inno 安装器是独立产物，ZIP 不是安装器的中间输入。

## Inno 安装器

Inno 安装器由现有脚本显式生成：

```powershell
pwsh -NoProfile -File .\scripts\package-inno.ps1
```

[`package-inno.ps1`](../scripts/package-inno.ps1) 从 App 项目文件读取产品版本，并通过 `MyAppVersion` 传给 [`CodexQuotaTray.iss`](../installer/CodexQuotaTray.iss)。安装器定义没有默认版本；未传入 `MyAppVersion` 时预处理必须 fail-closed，禁止静默使用旧版本。

脚本默认先 publish，验证应用 EXE 和 PE 图标，再把完整 publish 目录交给 Inno。Inno 编译需要显式可用的 `ISCC.exe`，脚本不会自动安装工具。

安装器当前定义为：

- per-user、无需管理员权限；
- 安装到当前用户程序目录；
- 使用固定 AppId 进行原位升级；
- 安装和卸载前通过 Production `--shutdown-existing` 请求已有实例正常退出；
- 可选当前用户开机启动；
- 默认卸载用户数据，交互模式或 `/KEEPUSERDATA` 可保留。

这些是安装器实现语义，不代表 Windows 10/11 上的安装、升级和卸载矩阵已经验收。

## 显式 smoke 与人工验收

Explorer 托盘 smoke 只能在交互式 Windows Explorer 桌面、关闭所有现有 CodexQuotaTray 实例后显式运行。统一验证脚本默认使用 Preview 身份；Production 必须额外明确选择。Smoke 不属于默认 Quick、Full 或 Release。

公开发布前至少人工确认：

- Windows 10 x64；
- Windows 11 稳定渠道；
- DPI、多显示器、任务栏位置和高对比度矩阵；
- 主面板、设置、About、托盘菜单和通知；
- 安装、原位升级、卸载、启动项和数据保留；
- Explorer 重启、睡眠恢复、网络恢复和长期资源稳定性；
- 官方 Usage 导航；
- 签名和公开校验和验证。

当前未完成事项以 [Roadmap](ROADMAP.md) 为准，不能用预览系统或归档 Rust 结果替代当前 WinUI 门禁。

## 签名与发布

当前本地产物未签名，只能标记为 developer build，可能触发 Windows SmartScreen。

公开发布需要组织控制的 Authenticode 证书或 Trusted Signing 身份。发布环境应：

1. 在受控提交和固定工具链上重新验证并构建。
2. 对需要分发的 PE 和脚本执行组织批准的签名流程。
3. 验证每个签名。
4. 在签名后重新生成 ZIP manifest 和公开 SHA-256。
5. 保留提交、工具链、签名身份和产物哈希的 provenance。

签名凭据只能位于签名服务或 HSM，不得进入仓库、日志或安装包。仓库不会自动执行签名或公开发布。

## 发布记录要求

正式发布记录至少应包含：

- 来源提交和分支；
- 从 App 项目读取的产品版本；
- Quick/Full/Release 结果及预期 skipped 项；
- publish、ZIP 和安装器文件名、大小和 SHA-256；
- 签名状态及验证结果；
- Windows、DPI、多显示器、安装生命周期和长期稳定性验收证据；
- 任何尚未关闭的限制。

没有对应产物、测试或人工证据时，不得在发布记录中标记完成。
