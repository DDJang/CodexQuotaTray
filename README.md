# CodexQuotaTray

[English](README.md) | [简体中文](README.zh-CN.md)

CodexQuotaTray is a pair of lightweight, read-only Codex quota clients. They do not use Electron,
WebView, web scraping, or account write APIs.

## Current architecture and data flow

| Client | Quota source | Token usage source | Implementation |
| --- | --- | --- | --- |
| Windows | User-selected local Codex CLI App Server (default) or read-only OAuth | User-selected local session ledger (default), Codex CLI account usage, or read-only OAuth account usage | C# / WinUI 3 in `windows/` |
| Android | OpenAI OAuth/Direct HTTPS and a paired Windows LAN source; quota priority is independent and defaults to OpenAI first | OpenAI Account usage and a paired Windows LAN source; Token priority is independent and defaults to Windows first | Kotlin / Jetpack Compose in `android/` |

> **Read-only boundary:** Both clients only read quota, Token usage, and reset-time data. They do not
> consume reset credits or perform account writes.

When phone synchronization is enabled on Windows, Android can read Windows quota and aggregated Token usage
over the private LAN. Conversation content, credentials, and raw account responses are not shared.

## Downloads

Official Windows and Android packages are available from
[GitHub Releases](https://github.com/DDJang/CodexQuotaTray/releases):

- Windows installer: `CodexQuotaTray-<version>-setup.exe`
- Windows portable: `CodexQuotaTray-<version>-win-x64.zip` (requires Windows App Runtime; the recommended installer handles this dependency automatically)
- Android APK: `CodexQuotaTray-Android-v<version>.apk`

Use the platform-specific `SHA256SUMS.txt` from the corresponding release to verify downloaded files.

## Code signing policy

See the [code signing policy](docs/CODE_SIGNING.md).

## Development entry points

- [WinUI build, run, and verification](windows/README.md)
- [Android build, run, and verification](android/README.md)
- [Product requirements](docs/PRD.md)
- [Architecture](docs/TECH_DESIGN.md)
- [Protocol contract](docs/API_CONTRACT.md)
- [Privacy boundaries](docs/PRIVACY.md)
- [Dependencies and licenses](docs/DEPENDENCIES.md)
- [Release process](docs/RELEASE.md)
- [Windows roadmap](docs/ROADMAP.md)
- [Android roadmap](docs/ANDROID_ROADMAP.md)
