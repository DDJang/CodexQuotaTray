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

使用 JDK 17、Android SDK 35 和仓库 Gradle Wrapper。按本机环境设置路径：

```powershell
Set-Location android
$env:JAVA_HOME = '<jdk-17>'
$env:ANDROID_HOME = '<android-sdk>'
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME

.\gradlew.bat :app:testDebugUnitTest :app:assembleDebug
```

运行前必须先用 `java -version` 和 `.\gradlew.bat --version` 确认实际 JVM；不要只依赖 PATH。
当前 Windows 开发机的 PATH 会命中 `C:\Windows\System32\java.exe`（Java 1.7），不能运行本项目。
本机已验证可用的现有 Gradle JVM 是
`C:\Users\18456\.jdks\jbr-21.0.11`，它运行 Gradle 8.11.1/AGP 8.9.1，同时项目仍按上面的
Java/Kotlin 17 目标编译。验证时只在当前 PowerShell 进程设置：

```powershell
$env:JAVA_HOME = 'C:\Users\18456\.jdks\jbr-21.0.11'
$env:Path = "$env:JAVA_HOME\bin;$env:Path"
$env:ANDROID_HOME = 'D:\Android\Sdk'
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
```

Android Studio 当前自带的 `D:\Android\Android Studio\jbr` 是 JDK 25，不用于本仓库的
Gradle 8.11.1 验证。不要为选择本机 JDK 修改 Gradle、AGP、SDK、`gradle.properties` 或提交
机器专属项目配置。

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
