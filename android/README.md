# CodexQuotaTray Android

`android/` 是个人使用的独立 APK。额度主路径为 Android App 私有 OAuth 凭据调用 Direct HTTPS
usage API；Windows 配对只提供 Token 使用量同步，以及 Direct 网络失败时的可选额度快照 fallback。
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
