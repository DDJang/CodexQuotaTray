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

On Windows, the local session source reads only the numeric fields and timestamps needed from Codex
`token_count` events and stores a daily aggregate. The Codex CLI and OAuth Token sources are separate
account-usage projections; they are not merged with the local session ledger. Quota and Token source
selection are independent, and switching a source switches to that source's cache and projection.

On Android, quota and Token have separate priority settings. Each Router tries sources in the configured
order and tries the other source when the preferred source fails or is unavailable. The OpenAI provider
requires OAuth; the Windows provider requires an explicit pairing and an available private LAN. The two
domains keep separate refresh, commit, and background-worker paths. Without either source for a domain,
that domain has no available data source.

When the user explicitly enables phone synchronization on Windows, Android can read the Windows aggregate
Token usage and the last successful quota snapshot over the private LAN. Conversation content, credentials,
and raw account responses are not shared.

## Downloads

Official Windows and Android packages are available from
[GitHub Releases](https://github.com/DDJang/CodexQuotaTray/releases):

- Windows installer: `CodexQuotaTray-<version>-setup.exe`
- Windows portable: `CodexQuotaTray-<version>-win-x64.zip`
- Android APK: `CodexQuotaTray-Android-v<version>.apk`

Use the platform-specific `SHA256SUMS.txt` from the corresponding release to verify downloaded files.
Versioning, automatic updates, and artifact/release rules are defined in the [release documentation](docs/RELEASE.md).

Formal releases are made from a platform tag on `main`: PR CI is the pre-merge check, and the platform
Release workflow performs the final tests, Release build, signing, artifact, SHA256, release-note, and
manifest validation.

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
