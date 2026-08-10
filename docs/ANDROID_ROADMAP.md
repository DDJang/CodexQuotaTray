# CodexQuotaTray Android Roadmap

这是 CodexQuotaTray 的个人使用、实验性 Android 路线。Android 与 Windows + WinUI
相互独立；Android 工作不得修改 `winui/`。当前目标是一个轻量、只读、个人使用的
独立 APK，不是多用户产品，也不以商店发布、隐私合规或通用 Codex 客户端为目标。

## 已验证基线

| 阶段 | 目标 | 状态 | 已验证结果 |
| --- | --- | --- | --- |
| P0 | Android ARM64 Codex runtime feasibility | Go | 真实 ARM64/Termux App Server 探测通过 |
| P0.5 | Standalone APK / embedded runtime | Go | 独立 APK、native runtime、App Server 和进程清理通过 |
| P1 | Authentication + real quota integration | Go | App 内登录、真实额度和认证持久化通过 |
| P2 | Product quota UI | Go | 动态窗口、重置时间、手动刷新和重启复验通过 |
| P3 | Minimal polish / recovery / packaging | Go | UI、图标、受限恢复，以及当时的 Debug/Release 构建与真机 smoke 通过 |

P0–P3 是历史基线，不代表当前日常产品路径仍然启动 Codex runtime。P0/P0.5 的
Python 探针、历史结果和 Termux Bridge 保留用于开发诊断；当前 APK 的正常额度路径
已经切换为 Direct HTTPS。

## 当前实现轮次

这一轮把日常额度链路收敛为：

```text
App-private OAuth Store
        -> OAuth device login / refresh
        -> GET https://chatgpt.com/backend-api/wham/usage
        -> dynamic quota state
        -> UI / WorkManager refresh / notifications
```

当前实现包含：

- 最小 OAuth device-code 登录、refresh token 轮换和 Android Keystore 加密的 App 私有持久化；
- 旧 `filesDir/codex-home/.codex/auth.json` 到新 OAuth Store 的一次性读取迁移；成功加密保存后
  尽力删除旧文件，且不再重新导入；
- Direct usage API 的 0、1 或多个动态窗口、missing/null、zero-windows 和 unavailable
  语义；
- 剩余额度与已配对 Windows Token 使用量各自独立的 WorkManager 周期刷新；
- 50/20/10% 跨阈值提醒及 reset/recovery 提醒；
- 失败分类、手动刷新和通知权限拒绝后的可用 UI。
- 与 quota 独立的 Windows Token Usage 私网按需同步、加密配对、本地聚合缓存和使用统计页。

上述链路由脱敏单元测试覆盖；OAuth、网络切换、WorkManager 触发和系统通知仍以当前真机
环境为准，需在每次涉及它们的改动后补做针对性验证。

## 后续范围

当前后续重点不再增加新的阶段编号：

1. 持续验证和修正后台刷新、通知在真实设备上的可靠性；
2. Widget；
3. 开机启动。

后台刷新和通知已经实现，Widget、开机启动尚未实现。维护现有登录、额度读取、协议适配、
Windows LAN 配对和构建/发布链路属于基线维护，不视为新的产品目标。

## 必须保持的语义与边界

- Direct usage API 是 Android 日常额度读取的唯一产品兼容层；不得在正常刷新路径中
  重新引入 App Server、WebSocket、Termux Bridge、HTTP 中转或 `ProcessBuilder`。
- P0/P0.5 的 App Server 和 Termux Bridge 只作为历史开发诊断工具保留。
- 动态渲染全部额度窗口，不把 `primary`、`secondary` 固定解释为特定周期。
- `remainingPercent` 优先由真实 `used_percent` 计算；缺失、null 或 malformed 不等于零。
- 额度窗口可以为 0、1 或多个；无窗口和额度详情不可用必须能明确表达。
- 有 `reset_at` 才显示绝对/相对重置时间；没有时不伪造。
- usage API 的 401/403、网络错误、服务器错误和非法响应必须保持可区分的错误路径。
- 普通启动和普通刷新不无条件 refresh；access token 临近过期或 usage 返回 401/403 时
  才尝试 refresh token。登录成功后的首次读取允许立即刷新认证状态。
- 不记录 token、Cookie、邮箱、完整账户响应、设备码、设备序列号或 opaque ID。
- Android 当前仍是个人使用的轻量只读 APK，不消费 reset credit，也不执行账户写操作。

## 当前非目标

除现有后台刷新/通知的可靠性维护、Widget 和开机启动外，其余新增产品能力均不在当前范围，包括：

- Play Store 或其他应用商店发布、多用户、多账号、团队管理或隐私合规产品化；
- 完整 Codex 聊天、会话历史、Agent、项目功能或远程访问；
- Codex runtime 自动更新、Termux 运行时依赖、Termux:Boot、proot、root、Shizuku、
  JNI/NDK 重写或外部 shell service；
- 通用 HTTP Bridge、云端同步、SQLite/Room 或通用插件框架；
- reset-credit 展示、消费、购买或其他账户写操作；
- 多 ABI、x86 模拟器兼容矩阵或应用商店分发。

开机启动指 Android APK 自身恢复刷新能力，不指 Termux:Boot。

## 当前验证边界

- 已验证历史设备：真实 Android `arm64-v8a` 手机。
- 已验证历史 runtime：DioNanos `codex-termux` `v0.146.0` Android ARM64 输入。
- 固定构建基线：AGP `8.9.1`、Gradle `8.11.1`、JDK 17、compile/target SDK 35。
- 已验证历史能力：登录持久化、真实 App Server 额度、手动刷新、force-stop/reopen、
  进程清理、一次受限 App Server 恢复、adaptive icon，以及当时的 Debug/未签名 Release 构建。
- 当前 Direct HTTPS/OAuth/WorkManager/通知链路必须保持脱敏单元测试覆盖；真机验证按每轮
  涉及的行为单独执行。
- 日常本地开发只构建、安装和运行 Debug；正式 Android Release 只由 GitHub Actions 从
  `main` 上的 `android-v*` tag 使用 Secrets 签名并发布。多设备兼容矩阵不属于当前路线门禁。
