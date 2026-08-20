# CodexQuotaTray

CodexQuotaTray 是一组轻量、只读的 Codex 额度客户端，不使用 Electron、WebView 或网页抓取。

| 客户端 | 数据来源 | 入口 |
| --- | --- | --- |
| Windows | 额度：本机 `codex app-server --stdio`；Token：本机 Codex session 日聚合 | C# / WinUI 3，位于 `windows/` |
| Android | 有 OAuth 时使用 App 私有 OAuth + Direct HTTPS usage API；无 OAuth 但已配对 Windows 时可直接使用 Windows LAN 额度快照 | Kotlin / Jetpack Compose，位于 `android/` |

两端都只读取额度和重置时间，不消费 reset credit，不执行账户写操作。Windows 统计页会扫描
本机 Codex session 文件中的 `token_count` 事件并展示日聚合；用户明确启用手机同步后，才会向
已配对 Android 返回聚合使用量。扫描不会提取或传输对话正文。

Android 的额度数据源是 OAuth 或已配对 Windows 二者之一：有 OAuth 时 Direct HTTPS 永远优先，
只有 Direct 网络失败才使用 Windows fallback；没有 OAuth 但已有 Windows pairing 时，页面可以直接
读取 Windows 最后成功额度快照并执行 Windows-only 刷新；两者都没有时才是没有额度数据源。

## Windows 功能与下载

Windows 客户端在系统托盘中显示额度和重置时间，提供额度重置提醒、自动刷新以及本机 Codex
session 的聚合 token 统计；它只读额度，不消费 reset credit，也不执行账户写操作。

正式 Windows 安装包和 portable x64 ZIP 只从
[GitHub Releases](https://github.com/DDJang/CodexQuotaTray/releases) 下载：选择
`CodexQuotaTray-<version>-setup.exe` 使用安装器，或选择
`CodexQuotaTray-<version>-win-x64.zip` 使用免安装包；同一 Release 的 `SHA256SUMS.txt`
用于校验下载文件。安装器可从 Windows“已安装的应用”中卸载，并允许选择是否保留用户数据。
版本、自动更新清单和产物规则见[统一发布流程](docs/RELEASE.md)。

## Code signing policy

请参阅[Code signing policy](docs/CODE_SIGNING.md)。

## 开发入口

- [WinUI 构建、运行与验证](windows/README.md)
- [Android 构建、运行与验证](android/README.md)
- [统一产品需求](docs/PRD.md)
- [架构设计](docs/TECH_DESIGN.md)
- [协议合同](docs/API_CONTRACT.md)
- [隐私边界](docs/PRIVACY.md)
- [依赖与许可证](docs/DEPENDENCIES.md)
- [统一发布流程](docs/RELEASE.md)
- [Windows Roadmap](docs/ROADMAP.md)
- [Android Roadmap](docs/ANDROID_ROADMAP.md)

日常开发只运行 Windows Debug/Dev 与 Android Debug。正式版本只从 `main` 上的平台专属 tag
通过 GitHub Actions 发布，具体规则以 [发布文档](docs/RELEASE.md) 为准。
