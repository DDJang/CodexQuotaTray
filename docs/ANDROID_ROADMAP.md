# CodexQuotaTray Android Roadmap

这是 CodexQuotaTray 的个人使用、实验性 Android 路线。当前产品是一个独立的
`arm64-v8a` APK：内置 Android ARM64 Codex runtime，在 App 内完成认证并读取真实
额度，不依赖 Windows、Termux、Python、Node/npm、云服务器或其他外部 App。

Android 与 Windows + WinUI 路线相互独立。Android 工作不得修改 `winui/`，除非项目
所有者明确要求跨平台同步。

## 当前基线

| 阶段 | 目标 | 状态 | 已验证结果 |
| --- | --- | --- | --- |
| P0 | Android ARM64 Codex runtime feasibility | Go | 真实 ARM64/Termux App Server 探测通过 |
| P0.5 | Standalone APK / embedded runtime | Go | 独立 APK、native runtime、App Server 和进程清理通过 |
| P1 | Authentication + real quota integration | Go | App 内登录、真实额度和认证持久化通过 |
| P2 | Product quota UI | Go | 动态窗口、重置时间、手动刷新和重启复验通过 |
| P3 | Minimal polish / recovery / packaging | Go | UI/图标、一次受限恢复、Debug/Release 构建和真机 smoke 通过 |

P0–P3 已完成。Python 探针、Termux Bridge 和 Android 诊断代码继续保留为开发回归
工具，但不是 APK 运行时依赖，也不是后续产品路线。

历史构建与真机证据见 [`android/P0_5_RESULT.md`](../android/P0_5_RESULT.md)，当前
构建、安装和诊断命令见 [`android/README.md`](../android/README.md)。

## 后续范围

P3 之后只推进以下四项产品能力，不再增加新的阶段编号：

1. 后台自动刷新；
2. 通知；
3. Widget；
4. 开机启动。

这四项的实现顺序和验收细节在各自开工轮次中确定。维护现有登录、额度读取、runtime
兼容性和构建链路属于基线维护，不视为新的产品目标。

## 必须保持的语义与边界

- 动态渲染全部额度窗口，不把 `primary`、`secondary` 固定解释为特定周期。
- 使用 `windowDurationMins` 和服务端名称生成展示标题；当前已支持 5 小时、7 天及
  其他可推导时长。
- 缺失、`null`、malformed 不等于零；零窗口是合法且可明确表达的状态。
- `account/rateLimits/read` 是否成功与窗口数量独立判断。
- 普通 `account/read` 保持 `refreshToken=false`；只有登录完成确认使用
  `refreshToken=true`。
- App Server 是唯一协议对端；不增加 HTTP Bridge、云端中转或账户写请求。
- 不记录 token、Cookie、邮箱、完整账户响应、设备码、设备序列号或 opaque ID。
- 当前支持范围保持为个人使用的 `arm64-v8a` 独立 APK。

## 当前非目标

除“后台自动刷新、通知、Widget、开机启动”外，其余新增产品能力均不在当前项目范围，
包括但不限于：

- Play Store 或其他应用商店发布；
- 多账号、多用户、团队管理或隐私合规产品化；
- 完整 Codex 聊天、会话历史、Agent 或项目功能；
- 自动更新 Codex runtime 或应用内更新；
- 多 ABI、x86 模拟器、root、Shizuku、JNI/NDK 重写；
- Termux 运行时依赖、Termux:Boot、proot 或外部 shell service；
- HTTP 服务、远程访问、云端 Bridge；
- Room/SQLite、复杂设置页或通用插件框架；
- reset-credit 展示、消费、购买或其他账户写操作；
- 面向公开分发的安装、签名、商店素材和发布自动化。

开机启动指 Android APK 自身恢复后台刷新能力，不指 Termux:Boot。

## 当前验证边界

- 已验证设备：真实 Android `arm64-v8a` 手机。
- 已验证 runtime：DioNanos `codex-termux` `v0.146.0` Android ARM64 输入。
- 固定构建基线：AGP `8.7.3`、Gradle `8.9`、JDK 17、compile/target SDK 35。
- 已验证：登录持久化、真实额度、手动刷新、force-stop/reopen、进程清理、一次受限
  App Server 恢复、adaptive icon、Debug 和未签名 Release 构建。
- 正式 signing key、公开分发、多设备兼容矩阵不属于当前路线门禁。
