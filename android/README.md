# CodexQuotaTray Android P2

这是个人使用的实验性 Android APK。P0、P0.5、P1 和 P2 已在真实 Android ARM64
手机上通过核心产品链路；当前 P3 只做后续的最小 polish/recovery/packaging，不实现
后台服务、通知、Widget、设置页或 Termux Bridge。

主路线和门禁见 [`docs/ANDROID_ROADMAP.md`](../docs/ANDROID_ROADMAP.md)。现有
`android/poc/p0_handshake.py` 和 `android/bridge/` 仅用于 P0/P1 诊断回归，不是
最终 APK 的运行时依赖。

## Runtime 准备

P0.5 需要一份真实的 Android ARM64 runtime 目录或压缩包。当前验证对象是
Codex Termux `v0.146.0` 的 ARM64 release/package；构建前必须确认输入中存在：

```text
bin/codex.bin                 # Android ARM64 ELF native executable
bin/libc++_shared.so          # 若该版本随包发布
bin/codex                     # launcher，可存在但不是 APK 的入口
bin/codex.js                  # launcher 脚本，不是完整 runtime
```

本轮构建验证使用 DioNanos `v0.146.0` release 的
`mmmbuto-codex-cli-termux-0.146.0.tgz`，SHA-256 为
`fcb7b2315443c7145f30be67ff099c965364be85b0daf8a20237042172f18533`。
压缩包中的 `package/bin/codex.bin` 已确认是 AArch64 ELF，伴随库为
`package/bin/libc++_shared.so`。

不要把只有 `codex.js` 或 shell launcher 的目录作为 runtime。Gradle 任务会检查
`codex.bin`/`codex` 的 ELF 文件头，并将 native executable 原样复制为生成 JNI
库目录中的 `arm64-v8a/libcodex_exec.so`，同时复制 `libc++_shared.so`。这些文件
随后由 Android APK 安装到 `applicationInfo.nativeLibraryDir`；不会解压 ELF 到
`filesDir` 后执行。runtime 不提交到仓库，也不复制 Termux 的认证文件。

## 构建

工程已附带标准 Gradle Wrapper 8.9；AGP 8.7.3 的构建需要 JDK 17。使用已安装的
Android SDK Platform 35 和 JDK 17。PowerShell 示例：

```powershell
Set-Location android
$env:JAVA_HOME = '<path-to-jdk-17>'
$env:ANDROID_SDK_ROOT = '<path-to-android-sdk>'
$env:ANDROID_HOME = $env:ANDROID_SDK_ROOT
$env:CODEX_ANDROID_RUNTIME = '<path-to-codex-cli-termux-0.146.0>'

.\gradlew.bat :app:assembleDebug `
  -PcodexAndroid.runtimeVersion=0.146.0 `
  -PcodexAndroid.port=43128
```

也可以把 runtime 路径直接作为 Gradle property 传入：

```powershell
.\gradlew.bat :app:assembleDebug `
  -PcodexAndroid.runtime='<path-to-codex-cli-termux-0.146.0>' `
  -PcodexAndroid.runtimeVersion=0.146.0
```

没有 runtime 输入时仍可构建一个开发诊断 APK，但页面会明确显示
`Runtime packaged: no`，这不算 P0.5 Go。构建输出应包含生成的
`lib/arm64-v8a/libcodex_exec.so` 和 `lib/arm64-v8a/libc++_shared.so`。

## 真机安装与运行

P0.5 只支持 `arm64-v8a`。安装并启动 debug APK：

```powershell
$adb = '<path-to-android-sdk>\platform-tools\adb.exe'
& $adb devices
& $adb shell getprop ro.product.cpu.abi
& $adb install -r 'android\app\build\outputs\apk\debug\app-debug.apk'
& $adb shell am force-stop com.codexquotatray.android
& $adb shell monkey -p com.codexquotatray.android 1
```

打开后主页面会自动执行一次额度读取。点击 `刷新` 会重新启动一次前台只读读取；
未认证时显示 `登录 Codex`，优先请求 `chatgptDeviceCode`，如果当前上游拒绝
device-code 请求，则只在本次连接内 fallback 到 App Server 的 `chatgpt` browser flow。
页面不会显示 token、邮箱、完整认证文件、完整 RPC 响应或 stderr 正文；只显示额度、
登录和错误状态。登录等待时点击 `打开浏览器`，完成授权后保持 App 页面打开，App 会
用 `account/read {"refreshToken": true}` 确认登录，再读取 rate limits。

运行时通过 `applicationInfo.nativeLibraryDir/libcodex_exec.so` 启动，不依赖
Termux 的 `PATH`；`HOME` 和 `CODEX_HOME` 仍指向 App 私有的 `files/codex-home`。
首次安装没有认证时，主页面显示 `尚未登录 Codex` 是预期的未登录分支，不代表 runtime
启动失败。P1 登录由 Codex runtime 管理并写入 App 私有
`CODEX_HOME`；不要复制 Termux `auth.json`，也不要把认证文件、token 或浏览器数据
提交、打印或放入公开位置。

普通启动和普通刷新使用 `account/read {"refreshToken": false}`；只有登录完成后的同一
App Server 会话确认使用 `{"refreshToken": true}`，不会把主动 token 刷新变成普通读取
的默认行为。

## P2 主页面

主页面只显示日常额度信息：

- `loading`：正在读取 Codex 额度；
- `unauthenticated`：显示“尚未登录 Codex”和“登录 Codex”入口；
- `loaded`：显示账户类型、0 个或多个动态额度窗口、剩余百分比、进度条、可用的重置
  时间和更新时间；
- `error`：显示简短错误状态和“刷新”按钮。

`resetsAt` 按 App Server 合同作为 Unix seconds 读取；缺失时显示“重置时间未知”，不
根据窗口名称猜测 5 小时或 7 天。缺失/null 的百分比不显示为 0%。

## 当前真实状态

独立 APK 已在真实 Android ARM64 手机上验证：

- embedded Android ARM64 Codex runtime；
- Codex App Server、App 内认证和 authentication persistence；
- `account/read` authenticated、真实 `account/rateLimits/read`；
- 动态额度窗口、剩余百分比、重置时间、更新时间和手动刷新；
- force-stop/reopen 后仍可读取额度；
- process cleanup、`testDebugUnitTest` 和 `assembleDebug`。

当前主页面使用 Android 原生 Views 保持依赖最小；它是正式额度页面，不再是只展示
runtime/App Server 诊断字段的 P0/P1 PoC 页面。P0/P0.5 Python 探针和 Android 诊断代码
仍保留作开发工具。

## 来源与许可证说明

Android Kotlin/Gradle/UI 代码是本仓库内的独立重实现。曾参考
`aeewws/codex-mobile-oneapk` 的实现思路，但没有直接复制其源码片段或文件，因此不
引入该项目的代码依赖或额外许可证文件。

构建时使用的 DioNanos `codex-termux` Android ARM64 runtime 来自外部输入，不提交到
仓库；其 package metadata 标为 Apache-2.0，随输入提供的 NOTICE 还列出 OpenAI Codex
和 Ratatui MIT 版权信息。runtime 的来源、版本和 SHA-256 保留在
[`android/P0_5_RESULT.md`](P0_5_RESULT.md)。

## 真机验收

以下条件都满足，才能把 P0.5 标为 Go；历史结果见
[`android/P0_5_RESULT.md`](P0_5_RESULT.md)：

- 手机 ABI 是 `arm64-v8a`，运行时不依赖 Termux、Node/npm、Python 或桌面路径；
- APK 包含 `lib/arm64-v8a/libcodex_exec.so` 和 `lib/arm64-v8a/libc++_shared.so`，
  安装后 `applicationInfo.nativeLibraryDir` 下两个文件均存在；
- `Runtime packaged`、`Native library present`、`Runtime ready` 为 `yes`；
- `codex --version` 成功并显示预期版本；
- `App Server started`、`Initialize` 成功；
- `account/read` 和 `account/rateLimits/read` 有独立、可解释的结果；
- 有认证时能返回真实额度，`quota_window_count` 可以为零，但
  `Quota state (window data)` 必须明确为 `available`、`zero_windows` 或
  `unavailable`；
- 点击 Stop 或退出 App 后 `Process cleanup succeeded: yes`，且没有遗留的
  Codex 进程。

P1 和认证持久化已经在同一真实手机上完成：

- `Login` 显示 device code/verification URL 或 browser fallback URL；
- 浏览器授权后 `Authenticated: true`、`account/read: succeeded`；
- `account/rateLimits/read` 成功，窗口数可以是 0 或多个；
- force-stop 并重新打开 App 后认证仍有效且 rate limits 仍成功。

P2 真机验收：

- 已登录启动后直接显示真实额度主页面；
- 点击 `刷新` 后重新读取成功，状态回到 `loaded`；
- force-stop/reopen 后仍显示真实额度；
- P2 主页面显示真实窗口、剩余百分比、重置时间、更新时间；
- 未登录状态由 Android 单元测试覆盖，本轮未清除真实手机认证数据；
- 未登录时显示 `unauthenticated` 和登录入口，而不是诊断字段。

可用以下命令检查 APK 的 native library 目录、私有状态目录和残留进程；命令不会
打印认证文件内容：

```powershell
& $adb shell dumpsys package com.codexquotatray.android | Select-String -Pattern 'nativeLibraryDir|primaryCpuAbi'
& $adb shell run-as com.codexquotatray.android ls -l files/codex-home/.codex
& $adb shell ps -A | findstr /i codex
```

P0/P0.5 的 Python 探针和 Android 诊断代码继续保留用于开发回归；它们不属于正式主
页面。`quota_state` 只描述额度窗口数据的可用性/完整度，不是 reset-credit 状态；P2
不实现 reset-credit 五态。

## 本机离线检查

P0/P1 Python 诊断回归仍可在仓库根目录执行：

```powershell
python -X utf8 android/poc/test_p0_handshake.py
python -X utf8 android/bridge/test_bridge.py
python -m py_compile android/poc/p0_handshake.py android/poc/fake_codex.py
```

这些检查不能证明 Android ABI、普通 App native library 执行权限、动态库加载或
App Server WebSocket 兼容性；这些结果必须等待真实 ARM64 手机。

## 常见失败

- `Runtime packaged: no`：没有传 `CODEX_ANDROID_RUNTIME` 或
  `-PcodexAndroid.runtime`。
- 构建提示没有 native ELF：输入是 launcher/npm 脚本，需换成包含
  `bin/codex.bin` 和依赖库的 Android ARM64 package/release。
- `Runtime ready: no` 且为 `arm64-v8a` 之外：当前 PoC 不支持该 ABI。
- `Native library present: no`：检查 APK 是否包含两个 `arm64-v8a` native 文件，
  以及设备是否为 `arm64-v8a`。
- `codex --version` 失败：记录 ProcessBuilder 的完整 IOException message，检查
  `nativeLibraryDir`、`libcodex_exec.so` 的存在性/可执行性、`LD_LIBRARY_PATH`，
  并从 logcat 区分 `Permission denied`、`No such file`、`CANNOT LINK EXECUTABLE`
  和 SELinux `avc denied`。
- App Server 未启动或 WebSocket 超时：检查端口 `43128` 是否被占用、runtime 的
  `app-server --listen` 是否支持该版本，以及 `adb logcat` 中是否有系统拒绝信息。
- `尚未登录 Codex`：点击 `登录 Codex`，再点击 `打开浏览器` 完成授权；不要把
  token 或完整 auth 文件粘贴到 issue、日志或仓库。
- `login_start_rpc_error`：记录 RPC 错误分类即可。若 device-code 上游返回 403，
  当前 P1 会尝试 App Server 的 browser flow；若 fallback 也失败，检查手机网络和
  浏览器连通性。
- `Rate limits: rpc_error`：只记录字段名、错误分类和退出码；不要把完整 RPC
  error message 当作诊断日志保存。
- `Process cleanup succeeded: no`：停止后先检查 `ps -A`，清理残留后再继续
  真机验收。
