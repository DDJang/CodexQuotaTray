# CodexQuotaTray 发布流程

Windows 与 Android 版本完全独立，但所有正式 Release 都必须来自 `main`。本文件是 tag、现有平台
签名配置和产物规则的发布执行文档；具体执行逻辑以 `.github/workflows/windows-release.yml` 与
`android-release.yml` 为准。

## Windows 功能、下载与卸载

Windows 客户端是只读的系统托盘额度工具，显示额度和重置时间，提供重置提醒、自动刷新以及
本机 Codex session 的聚合 token 统计；它不消费 reset credit，也不执行账户写操作。

正式 Windows 产物发布在[GitHub Releases](https://github.com/DDJang/CodexQuotaTray/releases)：

- `CodexQuotaTray-<version>-setup.exe`：推荐的 per-user Inno Setup 安装器；
- `CodexQuotaTray-<version>-win-x64.zip`：portable x64 包（需先具备 Windows App Runtime）；
- `SHA256SUMS.txt`：对应 Release 产物的 SHA256 校验值。

CodexQuotaTray 保持 unpackaged；安装器在检测到共享运行时未满足要求时，从固定配置中的 Microsoft
URL 下载 Microsoft-signed x64 Windows App Runtime standalone installer，校验 SHA-256 后在首次
启动前以 `--quiet` 安装，并在安装后复检四个必需组件。满足要求时不会下载；runtime 安装器只在
安装阶段位于 `{tmp}`，不会被写入 setup 或 `{app}`，卸载 CodexQuotaTray 也不会移除共享 runtime。
固定版本、包 identity、下载源和校验信息集中在
[`windows-app-runtime.json`](../windows/installer/windows-app-runtime.json)。

下载显示可取消的进度页；准备或安装失败时提供重试说明和浏览器官方下载入口。网络受限时，可将该配置
对应的运行库文件按原文件名放在 setup 旁，安装器会复制到临时目录并校验同一 SHA-256 后使用；也可手动
安装该运行库后重试。静默安装失败仅返回错误，不打开浏览器或交互对话框。

安装器可通过 Windows“已安装的应用”卸载，并在卸载时明确询问是否保留用户数据。自动更新使用
统一更新清单；用户也可以直接从上述 Release 页面下载并手动启动安装器。

本项目的 [Code signing policy](CODE_SIGNING.md) 说明签名范围、团队角色、隐私政策和申请前状态。

## 平台规则

| 平台 | Tag | 版本来源 | Workflow | 产物 |
| --- | --- | --- | --- | --- |
| Windows | `windows-v<version>` | WinUI App 项目 `Version` | `windows-release.yml` | x64 ZIP、Inno installer、Windows `SHA256SUMS.txt` |
| Android | `android-v<version>` | `android/app/build.gradle.kts` 的 `versionName` | `android-release.yml` | `CodexQuotaTray-Android-v<version>.apk`、Android `SHA256SUMS.txt` |

Android 与 Windows 客户端都从固定地址
`https://raw.githubusercontent.com/DDJang/CodexQuotaTray/update-manifest/update-manifest.json`
读取统一更新清单，不查询 GitHub Releases API。安装包和 APK 仍由对应 GitHub Release 托管，清单中包含下载地址、SHA256、发布说明与发布时间。

Windows 与 Android Release workflow 可独立运行；只有各自的 `publish-manifest` job 使用共享的 `update-manifest-publish` concurrency group，且不取消运行中的 job。因此 manifest 写入会串行，但 Release job 的构建、签名、资产和 GitHub Release 阶段不因该 group 串行。Release 与全部资产成功创建后，`publish-manifest` job 读取 `update-manifest` 分支上的现有清单，只替换当前平台节点，再提交回该分支。首次创建该分支时使用 `.github/update-manifest.seed.json` 保留另一个平台已有的最新正式版本。

两个 workflow 都会 fetch `origin/main`，并拒绝不属于 `main` 历史的 tagged commit。Tag 版本必须
与对应项目版本完全一致。平台更新器只能识别自身平台的 tag 前缀，不能依赖 GitHub “latest release”。
Windows 与 Android 正式 Release 当前只支持严格的 `MAJOR.MINOR.PATCH` 版本，例如 `0.10.0`；
`preview`、`alpha`、`beta` 和 `rc` 后缀不属于当前发布合同。

## 日常开发边界

- Windows 日常运行应用只使用 Debug 的 **CodexQuotaTray Dev**；Quick/Full 都验证 Debug/Dev。影响
  Release 输出的开发改动可按 [AGENTS.md](../AGENTS.md) 在本地运行 `verify-winui.ps1 -Mode Release`
  进行 release-specific publish/artifact verification。
- Android 本地只构建/安装 Debug 的 **CodexQuotaTray Dev**，使用默认 debug 签名。
- 本地 `-Mode Release` 验证不安装、签名或发布 Production；Android 本地开发也不读取 Release JKS 或 secret。
- 正式 Production 的安装、产物构建与签名、平台 tag、GitHub Release 和正式发布只由既有发布状态机及
  GitHub Actions 从 `main` 上的平台 tag 完成。
- 真实账户、Explorer 托盘、系统通知和真机网络 smoke 都必须显式授权。

## 正式发布步骤

1. 在开发分支完成开发、review 和 PR CI。
2. PR CI 通过后 merge 到 `main` 并 push。
3. 确认 `main` 上的实际目标 commit、项目版本和 release notes 正确；Android `versionCode`
   必须大于既有 `android-v*` tag 历史中的最大值。
4. 在该 `main` commit 创建对应平台 tag 并 push。
5. Release workflow 执行 Release build、签名验证、产物校验、SHA-256 校验、release notes 和
   `update-manifest` 验证，再创建 GitHub Release。完整测试、lint、format 和 Debug build 由 PR CI
   负责；任何校验、签名或构建失败都不得发布。

PR CI 是合并前验证；Release workflow 是正式发布验证。发布流程不再把 merge 后的普通 main CI
作为额外的独立发布门禁。

普通 CI 由 PR 与 `workflow_dispatch` 触发；merge 到 `main` 不会再触发重复的普通 CI。

Windows workflow 运行 `windows/scripts/verify-winui.ps1 -Mode Release`（只做 release-specific publish
和产物检查），再生成 portable ZIP 和 Inno installer。上述 `publish-winui.ps1`、`package-winui.ps1`、
`package-inno.ps1` 属于正式 Release workflow 的底层 publish/package 实现，但不作为维护者日常手工
发布入口；正式发布入口仍由统一发布状态机和 GitHub Actions 管理。PR CI 的 Full 验证负责格式检查和离线测试。

Android workflow 使用 JDK 17、Android SDK、Gradle Wrapper，运行带正式签名的 `assembleRelease`，再
定位 SDK build-tools 中的 `apksigner` 验证签名。PR CI 负责测试、lint 和 Debug assemble。签名缺少任一 Secret 时 fail closed：

- `ANDROID_RELEASE_KEYSTORE_BASE64`
- `ANDROID_RELEASE_STORE_PASSWORD`
- `ANDROID_RELEASE_KEY_ALIAS`
- `ANDROID_RELEASE_KEY_PASSWORD`

Keystore 只在 runner 临时目录解码，不得提交到仓库或写入本地开发文档。
