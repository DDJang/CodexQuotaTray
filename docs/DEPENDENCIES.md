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
| DioNanos `codex-termux` Android ARM64 runtime | `v0.146.0` 外部输入 | package metadata 为 Apache-2.0；随包 NOTICE 另列 OpenAI Codex 和 Ratatui MIT 版权信息；不提交二进制 |
| Android Gradle Plugin | `8.7.3` | 构建工具 |
| Gradle Wrapper | `8.9` | 构建工具；wrapper 源文件按 Apache-2.0 头部分发 |
| OkHttp | `4.12.0` | Apache-2.0；本地 App Server WebSocket 传输 |
| JUnit | `4.13.2` | 单元测试依赖 |

Android Kotlin/Gradle/UI 代码为本仓库内的独立重实现。曾参考
`aeewws/codex-mobile-oneapk` 的实现思路，但未直接复制其源码片段或文件，因此不
引入该项目的代码依赖或额外许可证文件。
