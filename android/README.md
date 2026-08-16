# CodexQuotaTray Android

`android/` 是个人使用的独立 APK。有 OAuth 时额度主路径为 Android App 私有凭据调用 Direct HTTPS
usage API；没有 OAuth 但已有 Windows 配对时，可以直接读取 Windows 最后成功额度快照并执行
Windows-only 刷新。Direct 网络失败时，有 OAuth 的请求才允许使用已配对 Windows 的额度 fallback。
协议细节见 [API_CONTRACT](../docs/API_CONTRACT.md)。

## 当前结构

- `auth/`：设备代码 OAuth、refresh 和 Keystore 加密存储；
- `protocol/`：usage DTO、解析与错误分类；
- `quota/`：额度仓库、脱敏快照、提醒、前后台刷新和 Windows fallback；
- `usage/`：Windows 配对、Token 聚合同步、设备绑定缓存与独立 WorkManager；
- Activity / Compose：页面与设置；
- `app/src/test/`：不需要真实账户或网络的回归测试。

额度与 Token 后台任务彼此独立。回到前台时的自动读取/同步由应用级生命周期触发，使用本机
最后一次自动尝试的两分钟抑制窗口；手动刷新始终立即执行。底栏切换只改变页面状态，不触发
网络请求。Android 只在已配对且 Wi-Fi 可用时访问 Windows LAN；OAuth、quota 和 LAN 请求使用
各自有界超时。

## 本地 Debug 开发

使用仓库 Gradle Wrapper、Android SDK 35 和兼容的 Gradle JVM；项目 Java/Kotlin 编译 target 仍为
Java 17。`JAVA_HOME`、`ANDROID_HOME` 和 `ANDROID_SDK_ROOT` 应按本机环境设置，并且只作用于当前
shell 进程。运行前必须用 `java -version` 和 `.\gradlew.bat --version` 确认实际 JVM；不要只依赖 PATH。
维护者开发机的机器专属环境事实和 fail-closed 约束见根目录 [AGENTS.md](../AGENTS.md)，不应当
被当作普通开发者的安装路径。

```powershell
Set-Location android
$env:JAVA_HOME = '<compatible-gradle-jvm>'
$env:ANDROID_HOME = '<android-sdk-35>'
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME

.\gradlew.bat :app:testDebugUnitTest :app:assembleDebug
```

周期 WorkManager 是非精确定时；配置的 15/30/60 分钟表示周期调度策略，不保证对应分钟准点执行。
Android Doze、电池优化及 OEM 后台策略可能延迟执行。Xiaomi / HyperOS 等设备可能冻结后台 UID，
此时已经 eligible 的 Job 也可能直到 App 解冻后才执行。需要更可靠的后台刷新时，用户应允许 App
后台运行并调整对应的系统电池优化设置；这不是 CodexQuotaTray 对 OEM 冻结的绕过承诺。

## Android 16 局域网兼容检查

当前 target/compile SDK 以 [`app/build.gradle.kts`](app/build.gradle.kts) 为准；现阶段仍由
`INTERNET` 隐式授予局域网访问，不提前声明未来平台的 `ACCESS_LOCAL_NETWORK` 运行时权限。
在 Android 16 真机上可对 Debug 包执行以下 opt-in 回归，分别确认 Quota 与 Token 的前台请求、
后台 Worker、DNS-SD 换址和受限时的有界 `OFFLINE` 分类：

```powershell
adb shell am compat enable RESTRICT_LOCAL_NETWORK com.codexquotatray.android.debug
adb reboot
# 完成受限场景后恢复，并再次验证正常 LAN 同步
adb shell am compat disable RESTRICT_LOCAL_NETWORK com.codexquotatray.android.debug
adb reboot
```

该检查需要 Android 16 真机；普通 unit test / `assembleDebug` 不替代它，也不因此增加权限 UI。

Debug 使用 `com.codexquotatray.android.debug` 和名称 **CodexQuotaTray Dev**，采用默认 debug
签名，可与正式版同时安装且数据隔离：

```powershell
& "$env:ANDROID_HOME\platform-tools\adb.exe" install -r `
  '.\app\build\outputs\apk\debug\app-debug.apk'
```

日常开发不得索取、读取或使用 Release JKS、密码、alias 或本地 release signing 配置。正式
Android APK 只由 GitHub Actions Secrets 签名，统一流程见 [RELEASE.md](../docs/RELEASE.md)。

## 验证边界

单元测试覆盖解析、状态、缓存、调度、配对和 LAN 错误路径。ADB 安装、系统通知、网络切换、
电池优化和真实账户验证属于按任务显式授权的真机 smoke，不由普通 Gradle 构建代替。

当前后续方向见 [Android Roadmap](../docs/ANDROID_ROADMAP.md)，隐私边界见
[PRIVACY](../docs/PRIVACY.md)。
