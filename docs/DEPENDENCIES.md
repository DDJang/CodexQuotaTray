# Dependencies and licenses

依赖版本不在本文重复维护：Windows 以
[`windows/Directory.Packages.props`](../windows/Directory.Packages.props) 和各项目文件为准；Android
以 [`android/app/build.gradle.kts`](../android/app/build.gradle.kts)、根 Gradle 配置和 Wrapper
properties 为准。更新依赖时必须同步审查许可证、维护状态、产物大小和运行时影响。

## Windows

| 依赖 | 用途 |
| --- | --- |
| Microsoft.WindowsAppSDK | WinUI 3 编译时 SDK reference；版本以 `windows/Directory.Packages.props` 为准 |
| Microsoft Windows App Runtime | unpackaged WinUI 3 的共享运行时；固定的 x64 standalone installer 版本、下载源、SHA-256 和 Microsoft Authenticode publisher 以 [`windows-app-runtime.json`](../windows/installer/windows-app-runtime.json) 为准 |
| CommunityToolkit.Mvvm | ViewModel 与 MVVM 基础 |
| ZXing.Net | 本地生成 Windows 配对二维码 |
| MSTest.Sdk | 离线测试平台 |

Windows 客户端保持 unpackaged 普通 EXE；.NET runtime 继续使用 self-contained publish，用户不需要
另装 .NET。Windows App Runtime 使用固定配置中的 Microsoft-signed x64 standalone installer，
由 per-user Inno Setup 安装器嵌入、安装阶段提取到 `{tmp}` 并以 `--quiet` 执行，安装共享的
Windows App SDK Framework/Main/Singleton/DDLM packages；runtime installer 不会永久写入 `{app}`。
卸载 CodexQuotaTray 时不会卸载或删除这个共享 Windows App Runtime。

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
