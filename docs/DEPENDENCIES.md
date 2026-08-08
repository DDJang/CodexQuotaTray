# Dependencies and licenses

这份简表随便携包一起分发。Windows 开发工具链和启动方式见
[WinUI README](../winui/README.md)，Android 构建方式见
[`android/README.md`](../android/README.md)。

| 依赖 | 版本来源 | 用途 |
| --- | --- | --- |
| Microsoft.WindowsAppSDK | `winui/Directory.Packages.props` | WinUI 3 和 Windows App SDK 运行时 |
| CommunityToolkit.Mvvm | `winui/Directory.Packages.props` | ViewModel 与 MVVM 基础 |
| MSTest.Sdk | 测试项目 `.csproj` | 离线测试 |

Self-contained publish 还会包含 .NET runtime、Windows App SDK 的 native/runtime 文件和传递依赖。分发时应保留这些依赖各自要求的许可证说明。旧 Rust/Cargo 依赖只存在于 Git tag `archive/rust-win32-final`，不属于当前 WinUI 构建。

## Android personal-use APK

| 依赖 | 版本来源 | 许可证/用途 |
| --- | --- | --- |
| Android Gradle Plugin | `8.7.3` | 构建工具 |
| Gradle Wrapper | `8.9` | 构建工具；wrapper 源文件按 Apache-2.0 头部分发 |
| OkHttp | `4.12.0` | Apache-2.0；Direct HTTPS OAuth/usage 传输 |
| AndroidX WorkManager | `2.9.1` | Apache-2.0；约 15 分钟周期刷新调度 |
| JUnit | `4.13.2` | 单元测试依赖 |
| org.json | `20240303` | JSON 解析测试替身；仅 test runtime，不进入 APK |
| MockWebServer | `4.12.0` | Apache-2.0；usage/OAuth 脱敏 HTTP fixture 测试 |

历史 P0/P0.5 记录使用过 DioNanos `codex-termux` Android ARM64 runtime `v0.146.0`。
该 runtime 仅作为已完成可行性证据和开发诊断输入，不再由当前 APK 打包，也不属于
当前日常产品依赖。

Android Kotlin/Gradle/UI 代码为本仓库内的独立重实现。曾参考
`aeewws/codex-mobile-oneapk` 的实现思路，但未直接复制其源码片段或文件，因此不
引入该项目的代码依赖或额外许可证文件。
