# Dependencies and licenses

依赖版本不在本文重复维护：Windows 以
[`windows/Directory.Packages.props`](../windows/Directory.Packages.props) 和各项目文件为准；Android
以 [`android/app/build.gradle.kts`](../android/app/build.gradle.kts)、根 Gradle 配置和 Wrapper
properties 为准。更新依赖时必须同步审查许可证、维护状态、产物大小和运行时影响。

## Windows

| 依赖 | 用途 |
| --- | --- |
| Microsoft.WindowsAppSDK | WinUI 3 与 Windows App SDK runtime |
| CommunityToolkit.Mvvm | ViewModel 与 MVVM 基础 |
| ZXing.Net | 本地生成 Windows 配对二维码 |
| MSTest.Sdk | 离线测试平台 |

Self-contained publish 还包含 .NET runtime、Windows App SDK native/runtime 文件和传递依赖，
分发时应保留相应许可证。

## Android

| 依赖 | 用途 |
| --- | --- |
| Android Gradle Plugin、Gradle Wrapper、Kotlin | 构建工具链 |
| AndroidX Activity Compose、Compose UI/Foundation/Animation、Material 3 | UI 与系统控件 |
| Kyant Backdrop、Shapes | Compose 玻璃与形状绘制 |
| OkHttp | OAuth、Direct quota 与 LAN HTTP |
| ZXing Android Embedded | 扫描配对二维码 |
| AndroidX WorkManager、Core KTX | 后台调度与平台兼容 |
| JUnit、org.json、MockWebServer | 仅测试使用的离线 fixture 与 HTTP 验证 |

Android 当前不打包 Codex native runtime、Python Bridge 或外部 shell service。
