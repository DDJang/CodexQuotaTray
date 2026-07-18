# CodexQuotaTray

CodexQuotaTray 是一个轻量、只读的 Windows 系统托盘应用，通过本机 `codex app-server` 显示 Codex 额度窗口、剩余百分比和重置时间。

当前版本为 `0.1.4`，支持 Windows 10/11，使用 Rust 与原生 Win32/GDI 实现，不包含 Electron、WebView 或浏览器运行时。

## 功能边界

- 动态展示 App Server 返回的全部额度窗口，不把 `primary` 或 `secondary` 固定解释为特定周期。
- 展示剩余百分比、重置时间、倒计时以及 fresh、refreshing、stale、offline 等状态。
- 支持手动刷新、10 分钟兜底刷新、系统恢复/网络恢复刷新和额度阈值提醒。
- 使用 Codex CLI 已有的认证，不读取浏览器 Cookie、Token 文件、网页 DOM、对话或项目代码。
- 当前 App Server schema 不提供权威的重置次数；应用明确显示“暂未提供”，不会从 `credits.balance` 猜测。
- MVP 始终只读，不包含额度重置消费操作。

## 项目结构

| 路径 | 职责 |
|---|---|
| `src/` | App Server 管理、JSON-RPC、协议类型、额度模型、状态与 Win32 UI |
| `tests/` | 完全离线的单元/集成测试和匿名协议 fixture |
| `schemas/` | `codex-cli 0.137.0` 生成的协议基线与版本记录 |
| `assets/` | 应用图标、manifest 和 Windows 资源定义 |
| `scripts/` | 图标生成、ZIP/Inno Setup 打包及产物验证 |
| `packaging/` | ZIP 包内使用的安装/卸载脚本 |
| `installer/` | Inno Setup 安装器定义 |
| `docs/` | 产品、技术、协议、发布、隐私与依赖文档 |
| `examples/` | 有限时、脱敏的 runtime soak 工具 |

文档入口：

- [产品需求](docs/PRD.md)
- [技术设计](docs/TECH_DESIGN.md)
- [App Server 协议契约](docs/API_CONTRACT.md)
- [路线图](docs/ROADMAP.md)
- [构建与发布](docs/RELEASE.md)
- [隐私说明](docs/PRIVACY.md)
- [依赖与许可证](docs/DEPENDENCIES.md)

## 构建与测试

```powershell
cargo fmt --all -- --check
cargo check --all-targets
cargo clippy --all-targets --all-features -- -D warnings
cargo test --all-targets
git diff --check
```

测试使用 `tests/fixtures` 下的匿名数据，不需要真实 Codex 账户。

## 运行托盘应用

Debug 构建默认使用确定性的演示数据：

```powershell
cargo run --bin codex-quota-tray-gui -- --demo
```

Release 构建使用本机 Codex CLI：

```powershell
cargo build --release --bin codex-quota-tray-gui
.\target\release\codex-quota-tray-gui.exe
```

可用 `--codex-bin PATH` 指定 Codex 可执行文件。安装或卸载时可用 `--shutdown-existing` 请求现有实例正常退出。

卡片聚焦时：`Enter` 刷新，`Tab`/方向键切换按钮，`Space` 执行，`F10` 打开菜单，`Esc` 隐藏。

## 打包

生成并验证免管理员 ZIP 包：

```powershell
pwsh -NoProfile -File .\scripts\package.ps1
pwsh -NoProfile -File .\scripts\test-package.ps1
```

安装 Inno Setup 7 后生成正式安装器格式：

```powershell
pwsh -NoProfile -File .\scripts\package-inno.ps1
```

产物写入被 Git 忽略的 `dist/` 或 `dist-inno/`，不应直接提交到源码仓库。当前产物未签名，只能标记为 developer build；发布前请阅读 [发布指南](docs/RELEASE.md)。

## 协议升级

当前协议基线是 `codex-cli 0.137.0`。升级 Codex CLI 后需要重新生成 schema、更新 `schemas/CODEX_VERSION`、检查 schema diff，并补充匿名 fixture 回归测试：

```powershell
codex app-server generate-json-schema --out schemas
```
