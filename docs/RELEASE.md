# CodexQuotaTray 发布流程

Windows 与 Android 版本完全独立，但所有正式 Release 都必须来自 `main`。本文件是 tag、签名和
产物规则的唯一文档；具体执行逻辑以 `.github/workflows/windows-release.yml` 与
`android-release.yml` 为准。

## 平台规则

| 平台 | Tag | 版本来源 | Workflow | 产物 |
| --- | --- | --- | --- | --- |
| Windows | `windows-v<version>` | WinUI App 项目 `Version` | `windows-release.yml` | x64 ZIP、Inno installer、Windows `SHA256SUMS.txt` |
| Android | `android-v<version>` | `android/app/build.gradle.kts` 的 `versionName` | `android-release.yml` | `CodexQuotaTray-Android-v<version>.apk`、Android `SHA256SUMS.txt` |

Android 与 Windows 客户端都从固定地址
`https://raw.githubusercontent.com/DDJang/CodexQuotaTray/update-manifest/update-manifest.json`
读取统一更新清单，不查询 GitHub Releases API。安装包和 APK 仍由对应 GitHub Release 托管，清单中包含下载地址、SHA256、发布说明与发布时间。

两个 Release workflow 使用同一个 `update-manifest-publish` concurrency group，且不取消运行中的发布。Release 与全部资产成功创建后，workflow 的最后一步读取 `update-manifest` 分支上的现有清单，只替换当前平台节点，再提交回该分支。首次创建该分支时使用 `.github/update-manifest.seed.json` 保留另一个平台已有的最新正式版本。

两个 workflow 都会 fetch `origin/main`，并拒绝不属于 `main` 历史的 tagged commit。Tag 版本必须
与对应项目版本完全一致。平台更新器只能识别自身平台的 tag 前缀，不能依赖 GitHub “latest release”。
Windows 与 Android 正式 Release 当前只支持严格的 `MAJOR.MINOR.PATCH` 版本，例如 `0.8.0`；
`preview`、`alpha`、`beta` 和 `rc` 后缀不属于当前发布合同。

## 日常开发边界

- Windows 本地只构建/运行 Debug 的 **CodexQuotaTray Dev**；Quick/Full 都验证 Debug/Dev。
- Android 本地只构建/安装 Debug 的 **CodexQuotaTray Dev**，使用默认 debug 签名。
- 日常开发不生成、安装或签名 Production/Release，不读取 Release JKS 或 secret。
- 真实账户、Explorer 托盘、系统通知和真机网络 smoke 都必须显式授权。

## 正式发布步骤

1. 在开发分支完成测试和审查，将目标提交 fast-forward/merge 到 `main` 并 push。
2. 确认项目版本正确；Android `versionCode` 必须大于既有 `android-v*` tag 历史中的最大值。
3. 在目标 `main` commit 创建对应平台 tag 并 push。
4. Workflow 重新验证版本与 ancestry、运行平台测试、构建产物、生成该平台专属 SHA-256，再创建
   GitHub Release。任何校验、签名或构建失败都不得发布。

Windows workflow 运行 `windows/scripts/verify-winui.ps1 -Mode Release`，再生成 portable ZIP 和 Inno installer。
本地 `windows/scripts/publish-winui.ps1`、`windows/scripts/package-winui.ps1`、
`windows/scripts/package-inno.ps1` 仅供显式发布输入诊断，不是
正式发布路径。

Android workflow 使用 JDK 17、Android SDK、Gradle Wrapper，运行测试与 `assembleRelease`，再
定位 SDK build-tools 中的 `apksigner` 验证签名。签名缺少任一 Secret 时 fail closed：

- `ANDROID_RELEASE_KEYSTORE_BASE64`
- `ANDROID_RELEASE_STORE_PASSWORD`
- `ANDROID_RELEASE_KEY_ALIAS`
- `ANDROID_RELEASE_KEY_PASSWORD`

Keystore 只在 runner 临时目录解码，不得提交到仓库或写入本地开发文档。
