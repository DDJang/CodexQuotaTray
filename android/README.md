# CodexQuota Android v0.1.0

这是个人使用的实验性 Android 独立 APK，只支持 `arm64-v8a`。P0、P0.5、P1、P2
和 P3 已全部完成并通过真实手机验证；App 不需要 Termux，可在 App 内完成 Codex
登录、读取真实额度并手动刷新。

当前已验证能力：

- APK 内置 Android ARM64 Codex runtime；
- App Server、`/readyz`、WebSocket 和 `initialize`；
- App 内认证及认证持久化；
- `account/read` 和真实 `account/rateLimits/read`；
- 0、1 或多个动态额度窗口、剩余百分比、绝对/相对重置时间；
- 手动刷新、force-stop/reopen 和进程清理；
- 一次受限的 App Server 启动/连接恢复；
- Android 原生 Views 主页面和 adaptive launcher icon；
- Debug、未签名 Release 构建和可配置 signing 输入。

后续产品范围只有：后台自动刷新、通知、Widget、开机启动。其他新增能力不属于当前
项目目标，详见 [Android Roadmap](../docs/ANDROID_ROADMAP.md)。

## 运行结构

构建时把外部 runtime 中的 `codex.bin` 原样复制为：

```text
lib/arm64-v8a/libcodex_exec.so
lib/arm64-v8a/libc++_shared.so
```

安装后，App 从 `applicationInfo.nativeLibraryDir` 启动 `libcodex_exec.so`，并设置：

```text
HOME=<filesDir>/codex-home
CODEX_HOME=<filesDir>/codex-home/.codex
LD_LIBRARY_PATH=<applicationInfo.nativeLibraryDir>
```

Kotlin 直接连接本机 Codex App Server。Termux Bridge、Python P0 探针和 Android 诊断
代码只用于开发回归，不是 APK 运行时依赖。App 不复制 Termux 的 `auth.json`，也不
依赖 Termux 的 PATH、HOME、Node/npm 或 Python。

## Runtime 输入

当前验证对象是 DioNanos `codex-termux` `v0.146.0` Android ARM64 package。构建输入
可以是目录、`.zip`、`.tar.gz` 或 `.tgz`，其中必须包含真实 AArch64 ELF 和伴随库，
例如：

```text
bin/codex.bin
bin/libc++_shared.so
```

不要把只有 `codex.js` 或 shell launcher 的目录作为 runtime。Gradle 会检查 ELF
文件头，不会把 npm/Node launcher 当作 Codex executable。

当前验证 archive 的 SHA-256：

```text
fcb7b2315443c7145f30be67ff099c965364be85b0daf8a20237042172f18533
```

更完整的来源、构建指纹和真机证据见 [P0.5 真机结果](P0_5_RESULT.md)。runtime
二进制不提交到仓库。

## 构建

固定工具链：Gradle 8.9、AGP 8.7.3、JDK 17、compile/target SDK 35。

PowerShell 示例：

```powershell
Set-Location android
$env:JAVA_HOME = '<path-to-jdk-17>'
$env:ANDROID_SDK_ROOT = '<path-to-android-sdk>'
$env:ANDROID_HOME = $env:ANDROID_SDK_ROOT
$env:CODEX_ANDROID_RUNTIME = '<path-to-codex-cli-termux-0.146.0>'

.\gradlew.bat :app:testDebugUnitTest
.\gradlew.bat :app:assembleDebug `
  -PcodexAndroid.runtimeVersion=0.146.0 `
  -PcodexAndroid.port=43128
.\gradlew.bat :app:assembleRelease `
  -PcodexAndroid.runtimeVersion=0.146.0
```

也可以使用 `-PcodexAndroid.runtime='<runtime-path>'` 代替环境变量。没有 runtime
输入时仍能构建开发诊断 APK，但它不是可日常使用的产品 APK，主页面会报告 runtime
或 App Server 不可用。

### Release signing

未配置 signing 时，`assembleRelease` 生成未签名 APK。个人签名可以通过以下环境变量
或对应的 `codexAndroid.release*` Gradle property 提供：

```text
CODEX_ANDROID_RELEASE_KEYSTORE
CODEX_ANDROID_RELEASE_STORE_PASSWORD
CODEX_ANDROID_RELEASE_KEY_ALIAS
CODEX_ANDROID_RELEASE_KEY_PASSWORD
```

四项必须同时提供。不要把 keystore、密码或 signing properties 提交到仓库；相关
本地产物已由 `.gitignore` 排除。当前路线不把公开发布、商店签名或发布自动化作为
产品目标。

## 安装与日常使用

以下命令从仓库根目录执行：

```powershell
$adb = '<path-to-android-sdk>\platform-tools\adb.exe'
& $adb devices
& $adb shell getprop ro.product.cpu.abi
& $adb install -r 'android\app\build\outputs\apk\debug\app-debug.apk'
& $adb shell am force-stop com.codexquotatray.android
& $adb shell monkey -p com.codexquotatray.android 1
```

打开后主页面自动读取一次额度：

- 未认证时显示“尚未登录 Codex”和“登录 Codex”；
- 登录完成后的确认读取使用 `account/read {"refreshToken": true}`；
- 普通启动和普通刷新保持 `account/read {"refreshToken": false}`；
- 已认证时显示全部真实额度窗口、剩余百分比、进度条、绝对/相对重置时间和更新时间；
- 缺少 `resetsAt` 时显示“重置时间未知”，不伪造日期；
- `account/rateLimits/read` RPC error 显示简短错误，不在主页面暴露诊断字段；
- 点击“刷新”会重新执行一次前台只读读取。

当前真实账户响应曾返回 1 个 10080 分钟窗口，页面正确显示为“7 天额度”；若服务端
返回更多窗口，页面会全部渲染，不补造不存在的 5 小时或 7 天窗口。

## 当前验证状态

真实 Android ARM64 手机已验证：

- 安装独立 APK，不安装、不启动 Termux；
- native runtime、`codex --version`、App Server 和协议握手成功；
- App 内登录后 `authenticated=true`，真实额度读取成功；
- force-stop/reopen 后认证保持；
- 手动刷新成功，额度和更新时间更新；
- App Server 临时失败时最多进行一次受限恢复；
- UI、动态标题、绝对/相对重置时间和 launcher icon 真机显示正常；
- 停止后没有遗留 Codex 进程；
- `testDebugUnitTest`、`assembleDebug` 和 `assembleRelease` 通过；
- APK 内存在 `libcodex_exec.so` 和 `libc++_shared.so`。

P0–P3 均为 Go。正式 signing key、公开分发和多设备兼容矩阵不属于当前路线门禁。

## 开发诊断与离线回归

以下工具继续保留，但不属于正式主页面：

```powershell
python -X utf8 android/poc/test_p0_handshake.py
python -X utf8 android/bridge/test_bridge.py
python -m py_compile android/poc/p0_handshake.py android/poc/fake_codex.py
```

这些检查不能替代真实 ARM64 手机验证。诊断不得打印 token、邮箱、完整认证文件、
完整 RPC 响应、设备序列号或 opaque ID。

## 常见失败

- runtime 未打包：确认 `CODEX_ANDROID_RUNTIME` 或 `codexAndroid.runtime` 指向实际输入。
- 没有 native ELF：输入只有 launcher/npm 脚本，需要包含 `codex.bin` 的 Android
  ARM64 package。
- `CANNOT LINK EXECUTABLE`：检查 APK 中的 `libc++_shared.so` 和
  `LD_LIBRARY_PATH`。
- App Server 或 WebSocket 超时：检查端口 `43128`、runtime 版本支持和最小 logcat。
- “尚未登录 Codex”：使用 App 内登录入口完成授权，不要复制外部认证文件。
- Release signing 配置不完整：四项 signing 输入必须全部提供；仅验证未签名 release
  时应清除不完整的本地 signing 配置。
- 进程清理失败：使用 `adb shell ps -A` 检查残留，不保存 stdout/stderr 原文。

## 来源与许可证

Android Kotlin/Gradle/UI 代码是本仓库内的独立实现。项目曾参考
`aeewws/codex-mobile-oneapk` 的实现思路，但未直接复制其源码片段或文件。

DioNanos `codex-termux` Android ARM64 runtime 是外部构建输入，不提交到仓库；其
package metadata 标为 Apache-2.0，随包 NOTICE 另列 OpenAI Codex 和 Ratatui MIT
版权信息。依赖简表见 [Dependencies](../docs/DEPENDENCIES.md)。
