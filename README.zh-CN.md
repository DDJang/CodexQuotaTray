# CodexQuotaTray

[English](README.md) | [简体中文](README.zh-CN.md)

CodexQuotaTray 是一组轻量、只读的 Codex 额度客户端，不使用 Electron、WebView、网页抓取或账户写接口。

## 当前架构与数据流

| 客户端 | 额度来源 | Token 使用量来源 | 实现 |
| --- | --- | --- | --- |
| Windows | 设置中选择本机 Codex CLI App Server（默认）或只读 OAuth | 设置中独立选择本机 session 账本（默认）、Codex CLI 账户使用量或只读 OAuth 账户使用量 | C# / WinUI 3，位于 `windows/` |
| Android | OpenAI OAuth/Direct HTTPS 与已配对的 Windows LAN 来源；额度优先级独立设置，默认 OpenAI 优先 | OpenAI Account 使用量与已配对的 Windows LAN 来源；Token 优先级独立设置，默认 Windows 优先 | Kotlin / Jetpack Compose，位于 `android/` |

> **只读边界：** 两端只读取额度、Token 使用量和重置时间，不消费 reset credit，也不执行账户写操作。

启用 Windows 手机同步后，Android 可以通过私人 LAN 读取 Windows 的额度和聚合 Token 使用量。
不会共享对话正文、凭据或原始账户响应。

## 下载

Windows 和 Android 正式版本均从
[GitHub Releases](https://github.com/DDJang/CodexQuotaTray/releases) 下载：

- Windows 安装包：`CodexQuotaTray-<version>-setup.exe`
- Windows 免安装包：`CodexQuotaTray-<version>-win-x64.zip`
- Android APK：`CodexQuotaTray-Android-v<version>.apk`

使用对应 Release 中的平台 `SHA256SUMS.txt` 校验下载文件。

正式发布使用 `main` 上的平台 tag：PR CI 是合并前验证，平台 Release workflow 负责最终测试、Release
构建、签名、产物、SHA256、release notes 和 manifest 验证。

## 代码签名政策

请参阅[代码签名政策](docs/CODE_SIGNING.md)。

## 开发入口

- [WinUI 构建、运行与验证](windows/README.md)
- [Android 构建、运行与验证](android/README.md)
- [统一产品需求](docs/PRD.md)
- [架构设计](docs/TECH_DESIGN.md)
- [协议合同](docs/API_CONTRACT.md)
- [隐私边界](docs/PRIVACY.md)
- [依赖与许可证](docs/DEPENDENCIES.md)
- [统一发布流程](docs/RELEASE.md)
- [Windows 路线图](docs/ROADMAP.md)
- [Android 路线图](docs/ANDROID_ROADMAP.md)
