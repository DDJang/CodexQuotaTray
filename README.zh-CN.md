# CodexQuotaTray

[English](README.md) | [简体中文](README.zh-CN.md)

CodexQuotaTray 是一组轻量、只读的 Codex 额度客户端，不使用 Electron、WebView、网页抓取或账户写接口。

## 当前架构与数据流

| 客户端 | 额度来源 | Token 使用量来源 | 实现 |
| --- | --- | --- |
| Windows | 设置中选择本机 Codex CLI App Server（默认）或只读 OAuth | 设置中独立选择本机 session 账本（默认）、Codex CLI 账户使用量或只读 OAuth 账户使用量 | C# / WinUI 3，位于 `windows/` |
| Android | OpenAI OAuth/Direct HTTPS 与已配对的 Windows LAN 来源；额度优先级独立设置，默认 OpenAI 优先 | OpenAI Account 使用量与已配对的 Windows LAN 来源；Token 优先级独立设置，默认 Windows 优先 | Kotlin / Jetpack Compose，位于 `android/` |

> **只读边界：** 两端只读取额度、Token 使用量和重置时间，不消费 reset credit，也不执行账户写操作。

Windows 的本机会话来源只读取 Codex `token_count` 事件所需的数字字段和时间戳，并保存按日聚合结果。
Codex CLI 和 OAuth Token 来源是独立的账户使用量投影，不会与本机会话账本合并。额度和 Token 来源
选择彼此独立，切换来源时使用对应来源的缓存和投影。

Android 的额度与 Token 分别保存来源优先级。各自 Router 按设置顺序尝试来源；首选来源失败或暂不可用
时，会继续尝试另一来源。OpenAI 来源需要 OAuth，Windows 来源需要用户完成配对并且私人 LAN 可用。
两个领域使用相互独立的刷新、提交和后台 Worker 路径；某个领域两种来源都不存在时，该领域没有可用
的数据来源。

用户在 Windows 上明确启用手机同步后，Android 可以通过私人 LAN 读取 Windows 的聚合 Token 使用量和
最近一次成功的额度快照。不会共享对话正文、凭据或原始账户响应。

## 刷新与自动更新

- Windows 的额度和 Token 刷新设置彼此独立，分别支持 5 分钟、15 分钟、30 分钟和仅手动模式，默认
  为 15 分钟。
- Android 的额度与 Token 使用独立的前台和周期 WorkManager 路径。前台自动任务使用两分钟抑制窗口，
  手动刷新立即执行，底栏切换不会发起网络请求。
- 两端都读取固定的 `update-manifest` 来源。自动检查可独立开关，24 小时内最多自动检查一次；手动
  检查不受该间隔限制。Android 当前只有 GitHub 更新源可用，Gitee 尚未实现。下载后会在安装或启动
  前按发布的 SHA256 校验文件。

## 下载

Windows 和 Android 正式版本均从
[GitHub Releases](https://github.com/DDJang/CodexQuotaTray/releases) 下载：

- Windows 安装包：`CodexQuotaTray-<version>-setup.exe`
- Windows 免安装包：`CodexQuotaTray-<version>-win-x64.zip`
- Android APK：`CodexQuotaTray-Android-v<version>.apk`

使用对应 Release 中的平台 `SHA256SUMS.txt` 校验下载文件。版本、自动更新以及产物和发布规则见[发布文档](docs/RELEASE.md)。

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
