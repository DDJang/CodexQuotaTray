# Android Roadmap

## Current

- App 私有 OAuth + Direct HTTPS 动态额度读取。
- 额度与 Token 独立的 OpenAI/Windows 来源优先级与双向 fallback；OpenAI Account Token 读取
  `/wham/profiles/me`，Windows LAN Token 保留 Account/Local scope。
- 额度与 Token 使用量各自独立的回到前台自动刷新/同步、手动刷新和周期 WorkManager。
- 额度阈值/重置通知、主题、设置、脱敏日志和 transport/scope-aware Token cache。
- 支持 reset credit 的只读展示与过期提醒；不发起购买、兑换或消费请求。
- Debug/Release 双 application identity 与平台独立 GitHub Release。
- HyperOS/Xiaomi 4×2 quota Widget：使用最后成功 quota snapshot projection；Direct / Windows quota 成功刷新后主动更新，Xiaomi exposure refresh 只读取 Widget 本地 cache。

## Next

- 持续验证后台刷新、通知、网络切换和厂商电池策略下的真机可靠性。
- Android 应用自身的开机恢复调度。

## Non-goals

- Play Store 产品化、多用户、多账号、团队管理或云端同步；
- Codex 聊天、Agent、项目、会话正文访问或远程控制；
- embedded Codex runtime、Termux、外部 shell/HTTP Bridge、root、Shizuku 或 NDK 重写；
- reset-credit 的购买、兑换、消费或其他账户写操作；
- 通用 LAN 客户端、多 ABI/x86 兼容矩阵。

## Validation

- JDK 17、Android SDK 35、仓库 Gradle Wrapper。
- Kotlin/协议/持久化/UI 改动运行 `:app:testDebugUnitTest` 与 `:app:assembleDebug`。
- PR 中的 Android 相关改动由无 Release secret 的 Android CI 验证 Debug；merge 到 `main` 不再触发
  重复的普通 Android CI。
- ADB、OAuth、通知、网络切换和电池策略属于按任务授权的真机 smoke。
- 正式签名和 Release 只按 [统一发布流程](RELEASE.md) 执行。
