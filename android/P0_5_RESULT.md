# P0.5 真机结果

这是个人使用实验性 Android 路线的 P0.5 记录。记录只保留可复核的构建、运行和
协议状态，不包含 token、`auth.json`、device code、完整认证响应或完整原始日志。

## 环境与输入

- 设备 ABI：`arm64-v8a`。
- 验证方式：独立 APK，未安装、未启动 Termux。
- Codex 来源：DioNanos `codex-termux` Android ARM64 runtime。
- Codex 版本：`0.146.0`。
- Codex release archive SHA-256：
  `fcb7b2315443c7145f30be67ff099c965364be85b0daf8a20237042172f18533`。
- `codex.bin` SHA-256：
  `a5e4b6bb28bc413c1322b17d63282b6d99bccce7d1d5c972dedf0731a88856fc`。
- `libc++_shared.so` SHA-256：
  `430f7cde7c1a88042bb9e39ae1c1f4b9f5819f3bd95acd1453d2e02c63ba1870`。
- Android SDK Platform：35。
- AGP：`8.7.3`。
- Gradle：`8.9`。
- JDK：Temurin `17.0.20+8`。

## APK 与 native runtime

- APK：`android/app/build/outputs/apk/debug/app-debug.apk`。
- APK 大小：`111,300,791` bytes。
- APK SHA-256：
  `6515eef8eed6b34b3dbe1716645163aee94e676ae9fac49cf9412f2f8ba5a209`。
- APK 内确认存在：
  - `lib/arm64-v8a/libcodex_exec.so`；
  - `lib/arm64-v8a/libc++_shared.so`。
- 安装后的 native library 目录：`applicationInfo.nativeLibraryDir` 下的
  `lib/arm64` 目录；两个文件均可见。
- `libcodex_exec.so` 是构建阶段对原始 `codex.bin` 的重命名复制，未修改 ELF 内容。

## 真机结果

| 检查项 | 结果 |
| --- | --- |
| Runtime packaged | yes |
| Native runtime present | yes |
| Runtime extracted | not applicable；直接使用 native library 目录 |
| Runtime ready | yes |
| `codex --version` | `codex-cli 0.146.0` |
| App Server started | yes |
| `/readyz` | HTTP 200 |
| WebSocket | `onOpen` |
| `initialize` | success |
| `account/read` | succeeded |
| authenticated | false；App 私有 `CODEX_HOME` 尚未登录 |
| `account/rateLimits/read` | RPC error；未认证分支，独立记录 |
| quota window count | 0；本次未登录，不代表窗口为零 |
| malformed WebSocket JSON | 0 |
| process cleanup | yes |
| process return code | 0 |

## P0.5 判定

P0.5：**Go**。

本次通过的硬门禁是：普通 Android App 进程可以执行 APK native library 中的
Codex，`--version` 成功，App Server 启动并通过 `/readyz`，WebSocket 建连，
`initialize` 成功，`account/read` 返回明确结果，且进程能够清理。未登录导致的
`authenticated=false` 与 `account/rateLimits/read` RPC error 不掩盖上述独立结果，
也不把 `quota_window_count=0` 误判为真实零额度。

## P1/P2 非敏感验收补充

- App 内登录完成，`account/read` 返回 authenticated，`account/rateLimits/read` 成功。
- force-stop/reopen 后不重新登录，认证仍保持，真实额度仍可读取；这作为认证持久化
  smoke 记录，不单独形成 P0.6 长期阶段。
- P2 正式主页面显示动态额度窗口、剩余百分比、重置时间、更新时间和手动刷新结果；
  本次真机返回 1 个窗口。
- 未登录 UI 由单元测试覆盖；本轮没有为测试清除真实设备认证状态。

P0.5、P1 和 P2 的记录均不包含 token、`auth.json`、device code、设备序列号、完整
账户响应或完整原始日志。
