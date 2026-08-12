# CodexQuotaTray

CodexQuotaTray 是一组轻量、只读的 Codex 额度客户端，不使用 Electron、WebView 或网页抓取。

| 客户端 | 数据来源 | 入口 |
| --- | --- | --- |
| Windows | 额度：本机 `codex app-server --stdio`；Token：本机 Codex session 日聚合 | C# / WinUI 3，位于 `windows/` |
| Android | App 私有 OAuth + Direct HTTPS usage API；网络失败时可选用已配对 Windows 的 LAN 快照 | Kotlin / Jetpack Compose，位于 `android/` |

两端都只读取额度和重置时间，不消费 reset credit，不执行账户写操作。Windows 统计页会扫描
本机 Codex session 文件中的 `token_count` 事件并展示日聚合；用户明确启用手机同步后，才会向
已配对 Android 返回聚合使用量。扫描不会提取或传输对话正文。

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
