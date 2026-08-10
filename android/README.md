# CodexQuota Android v0.1.0

这是 CodexQuotaTray 的个人使用 Android 版本。它是一个轻量的独立 APK，当前主路径
不依赖 Windows、Termux、Python、Node/npm、HTTP Bridge 或 Codex App Server。

P0–P3 的历史 ARM64 runtime、App Server、登录、真实额度和 UI 验证仍保留在仓库中；
当前日常产品路径已经收敛为：

```text
App-private OAuth credentials
        -> GET https://chatgpt.com/backend-api/wham/usage
        -> dynamic quota windows
        -> quota UI / background refresh / notifications
```

当前实现范围：

- 设备代码 OAuth 登录、token refresh 和 App 私有持久化；
- 从旧的 `filesDir/codex-home/.codex/auth.json` 一次迁移认证信息，成功写入加密存储后尽力删除旧文件；
- Direct HTTPS 读取 `plan_type`、主/次窗口和 additional rate-limit 窗口；
- 缺失值保持未知，零窗口和额度详情不可用状态明确区分；
- Android 原生 Views 主页面、动态窗口名称、绝对/相对重置时间和手动刷新；
- WorkManager 支持 15 分钟、30 分钟和 1 小时后台刷新；
- 50/20/10% 跨阈值提醒和重置恢复提醒。
- 设置页支持浅色/深色主题、通知测试、电池优化引导和脱敏运行日志。
- 可选的 Windows Token Usage 私人局域网配对、按需同步、聚合缓存和 365 天活动热力图。
- 直接 HTTPS 额度读取发生网络失败时，可选使用已配对 Windows 的最后成功额度快照作为 Wi-Fi LAN fallback；Windows 不是默认额度来源。

## Windows Token Usage 同步

设置页优先点击“扫码配对”，扫码保存 `codexquota://pair?deviceId=...&host=...&port=43821&token=...`；
相机只在点击扫描后请求，拒绝相机时仍可粘贴 URI 或手动输入 Windows 私人 IPv4 与配对密钥。
配对信息使用独立 Android Keystore/AES-GCM 存储，不与 OAuth 凭据混合，退出 Codex 登录也不会删除配对。
已配对状态提供立即同步、重新扫码配对和解除配对。主页面底部的“统计”页第一次显示时会先显示上次成功缓存，
并根据“打开统计页时自动同步”设置决定是否同步一次；同一 MainActivity 生命周期内重复切换不会重复发起
自动同步，也可随时手动点击“同步”。
该功能不接入 quota WorkManager。

同一 Windows listener 还可在 direct usage API 的 `NETWORK` 失败时提供只读 `/v1/quota`
fallback。Android 只有在已配对、仍连接 Wi-Fi，且 direct quota 请求失败为网络错误时才会
请求它；认证失效、服务端错误和非法响应不 fallback。成功的 Windows quota 使用现有 quota
snapshot cache、提醒去重状态和 UI，只在更新时间后显示弱的 `· Windows` 来源标记。保存的
LAN 地址先直连，只有 offline 才做最多 3 秒的同 deviceId DNS-SD 发现并重试一次；401 不会
触发 discovery。Windows 未维护成功额度快照时返回非 2xx，绝不伪造零额度。

配对后的同步先直连保存的 `lastKnownHost`；只有离线连接失败才通过 Android `NsdManager` 在
`_codexquota._tcp` 上做一次 4 秒以内的短发现，匹配稳定 `deviceId` 后更新地址并重试。Windows
只发布 deviceId、电脑名和端口等非敏感 metadata，不发布 pairing secret。401 表示密钥失效，
不会触发发现，而会提示重新扫码；无 deviceId 的旧手动配置仍可直连但没有自动发现。

LAN 同步只接受 RFC1918 IPv4，禁用 redirect，连接/读取采用短超时。应用允许 cleartext
是为了这一受校验的 `TokenUsageSyncClient`；OpenAI OAuth 与 usage 客户端仍只使用固定
HTTPS 地址。LAN 同步仅适用于可信私人 Wi-Fi，不建议在不可信公共 Wi-Fi 使用。Windows
防火墙规则需用户自行确认，应用不会自动提权或修改防火墙。

后台刷新和通知的真实设备 smoke 仍需在当前 APK 上完成；本轮没有把它们描述为已经
通过真机验证。Widget 和开机启动仍属于后续范围，详见
[Android Roadmap](../docs/ANDROID_ROADMAP.md)。

## 构建

固定工具链：Gradle 8.9、AGP 8.7.3、JDK 17、compile/target SDK 35。Android SDK 示例
路径为 `D:\Android\Sdk`，请按本机实际 JDK 17 路径调整。

从仓库根目录执行：

```powershell
Set-Location android
$env:JAVA_HOME = '<path-to-jdk-17>'
$env:ANDROID_SDK_ROOT = 'D:\Android\Sdk'
$env:ANDROID_HOME = $env:ANDROID_SDK_ROOT

.\gradlew.bat :app:testDebugUnitTest
.\gradlew.bat :app:assembleDebug
.\gradlew.bat :app:assembleRelease
```

### Codex 沙箱构建与测试结果文件锁

在 Codex 沙箱中，Gradle 缓存和 ADB 都位于工作区外；首次 Gradle 或 ADB 命令应直接请求
相应权限。命令超时只会中断调用端，不一定会立即结束 Gradle daemon。若随后
`:app:testDebugUnitTest` 报告无法删除
`app/build/test-results/testDebugUnitTest/binary/output.bin`，说明前一次 daemon 仍持有测试
结果文件，而不是测试断言失败。

先使用与构建相同的 JDK 和 Android SDK 环境执行：

```powershell
.\gradlew.bat --stop
```

等待 daemon 退出后，再用一次具有足够超时的完整命令重跑测试和构建。不要手动删除
`app/build/test-results`，也不要结束未确认归属的 Java/Kotlin 进程；`--stop` 只会正常停止
Gradle daemon，能够安全释放这类测试结果锁。

### HyperOS 3 / Xiaomi 渲染兼容性

Android UI 已迁移到 Jetpack Compose 与 Kyant Backdrop 2.0。玻璃控件通过 Compose
图层 backdrop、runtime shader、lens、vibrancy、highlight 和 shadow 绘制，不再创建
QmDeve `LiquidGlassView`，也不再调用 HyperOS 的 `MiBackgroundBlurBlend` View 路径。
因此没有 Xiaomi/Redmi 品牌回退或禁用开关；HyperOS 3 与其他支持设备使用同一套真实
玻璃实现。后续若出现启动或渲染问题，应先检查 Compose/Kyant shader 路径，不能重新引入
QmDeve、位图截图或仅对 Xiaomi 关闭玻璃的分支。

Kyant 2.0 发布物声明 compileSdk 37，但当前仓库仍按 SDK 35 构建并保持原 targetSdk。
`android.experimental.disableCompileSdkChecks=true` 仅绕过这项 AAR 元数据检查；应用没有
调用 API 36/37 平台接口。若未来仓库统一升级到标准 Android SDK 37 与相应 AGP，可删除
该兼容设置并重新跑完整的 debug/release 验证。

当前构建不读取 `CODEX_ANDROID_RUNTIME`，也不会把 Codex native binary 打入 APK。
历史 runtime 输入、来源和指纹见 [P0.5 真机结果](P0_5_RESULT.md)，仅作为 P0/P0.5
证据和开发诊断记录。

未配置 signing 时，`assembleRelease` 生成未签名 APK。个人签名仍可通过以下四项
环境变量或对应 Gradle property 配置，四项必须同时提供：

```text
CODEX_ANDROID_RELEASE_KEYSTORE
CODEX_ANDROID_RELEASE_STORE_PASSWORD
CODEX_ANDROID_RELEASE_KEY_ALIAS
CODEX_ANDROID_RELEASE_KEY_PASSWORD
```

不要把 keystore、密码或本地 signing property 提交到仓库。

## 安装与日常使用

```powershell
$adb = 'D:\Android\Sdk\platform-tools\adb.exe'
& $adb devices
& $adb shell getprop ro.product.cpu.abi
& $adb install -r 'android\app\build\outputs\apk\debug\app-debug.apk'
& $adb shell am force-stop com.codexquotatray.android
& $adb shell monkey -p com.codexquotatray.android 1
```

首次打开时点击“登录 Codex”，按页面显示的设备代码完成浏览器授权。成功后 token
使用 Android Keystore 加密后保存到 App 私有存储；普通启动和普通刷新不会无条件重新
登录或刷新 token。访问令牌临近过期或 usage API 返回 401/403 时，App 才使用 refresh
token 更新认证信息。

当前 UI 行为：

- 主页面使用固定顶部标题和底部“额度 / 统计”双 Tab；统计页未配对时提供前往设置的 empty state；
- 未登录主页面只显示“登录 Codex”入口，登录流程在独立页面完成，成功后返回并读取当前额度；
- 已登录读取全部真实额度窗口，不把窗口数量固定为 1、5 小时或 7 天；
- `used_percent` 优先转换为 `100 - used_percent`，缺失值显示“剩余未知”；
- 有 `reset_at` 时显示绝对时间和相对剩余时间，没有时显示“重置时间未知”；
- 额度服务 401/403、网络错误、服务器错误和非法响应显示不同的简短错误状态；
- 设置首页按“账号 / 通知与同步 / 个性化 / 其他”分组；通知、同步、主题与 Windows 配对分别进入独立子页。
- 通知页提供系统通知、低额度提醒和额度重置提醒；系统通知关闭时，下面的提醒设置与通知测试不可操作。
- 同步页分别控制剩余额度后台刷新与 Token 使用量进入统计页时的自动同步；剩余额度刷新频率可选 15 分钟、30 分钟或 1 小时。
- 后台刷新成功后会保存最近一次脱敏额度快照；主页面在前台会即时接收结果，重新打开时也会先显示最近成功数据，再执行新刷新。
- 设置页提供“发送测试通知”和“打开电池设置”入口；前者用于确认系统通知权限，后者只提供 Android 电池优化引导，不自动修改系统策略。
- 设置页提供浅色/深色外观主题选择；运行日志页只显示脱敏摘要，并支持复制日志，复制按钮会短暂显示“已复制”并禁用。
- 设置页提供“Codex 额度账号”；已登录时显示经过掩码的账号标识并支持退出，未登录时可进入独立登录页。
- 关于页显示当前版本和 GitHub 项目链接，不影响额度读取。

## 认证迁移

如果此前 P0.5 APK 在以下路径留下了旧认证文件：

```text
<filesDir>/codex-home/.codex/auth.json
```

新版本首次读取时会解析其中的 OAuth token，写入新的 App 私有 OAuth Store；成功写入后
会尽力删除旧文件，并记录一次性迁移标记。迁移只在本机发生，不打印认证内容。无法
解析或缺少 access token 的旧文件会被忽略，需重新登录。加密凭据一旦存在，即使损坏
也不会回退读取旧文件；退出登录同样保留迁移标记，避免旧凭据被重新导入。

## 开发诊断与离线回归

P0/P0.5 的 Python 工具继续保留，但不属于 APK 日常运行时：

```powershell
python -X utf8 android/poc/test_p0_handshake.py
python -X utf8 android/bridge/test_bridge.py
python -m py_compile android/poc/p0_handshake.py android/poc/fake_codex.py
```

这些检查不能替代真实 ARM64 手机、OAuth 浏览器授权、后台调度和通知权限验证。诊断
不得打印 token、邮箱、完整认证文件、完整 HTTP 响应、设备序列号或 opaque ID。

## 真机验证清单

当前产品改动需要在真实 Android ARM64 手机上补做：

1. 新安装后设备代码登录；
2. force-stop/reopen 后直接读取 usage；
3. 401 后 refresh token 续期；
4. `rate_limit` 返回 0、1 和多个窗口；
5. 网络断开、401/403、500 和非法响应；
6. WorkManager 周期刷新；
7. 50/20/10% 跨阈值和 reset 通知；
8. 通知权限拒绝时主页面仍可正常刷新。

## 来源与许可证

Android Kotlin/Gradle/UI 代码是本仓库内的独立实现。项目曾参考
`aeewws/codex-mobile-oneapk` 的实现思路，但未直接复制其源码片段或文件。

历史 DioNanos `codex-termux` Android ARM64 runtime 不再随当前 APK 打包，也不提交到
仓库；其来源、版本和 SHA-256 仅记录在 [P0.5 真机结果](P0_5_RESULT.md)。依赖简表见
[Dependencies](../docs/DEPENDENCIES.md)。
