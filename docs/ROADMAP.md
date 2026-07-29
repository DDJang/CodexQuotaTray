# CodexQuotaTray Roadmap

状态日期：2026-07-29

当前状态：WinUI 3 已成为唯一开发和打包入口；Rust/Win32 实现已归档；公开发布门禁尚未全部完成。

## 已完成

- 只读 App Server JSONL transport、request ID 路由和受控子进程生命周期；
- typed protocol DTO、动态额度窗口和 reset-credit 只读投影；
- 单 in-flight 刷新协调、稀疏通知合并、失败保留和 stale/offline 状态；
- WinUI 托盘、动态面板、设置、提醒、启动项和系统事件；
- 非敏感 settings/cache/alert state 持久化；
- fake App Server、匿名 fixture 和离线测试；
- folder-based self-contained x64 publish；
- per-user Inno Setup 安装、升级、卸载和 KeepUserData 流程；
- Rust/Win32 最终验证版本归档到 `archive/rust-win32-final`；
- 当前分支移除 Rust 源码、Cargo、旧测试和旧 PowerShell ZIP 安装链。

## 当前维护边界

- MVP 始终只读，不提供额度重置消费操作；
- 不抓取网页，不读取 Cookie 或 Codex token 文件；
- UI 不解析原始 JSON-RPC；
- 不假设 `primary` 或 `secondary` 的固定周期；
- `schemas/**` 暂时保留为协议审计资料，不是运行时依赖；
- WinUI 便携 ZIP 与 Inno 安装器是两个独立输出，安装器直接读取 publish 目录；
- 不提交 `target/`、`dist/`、`dist-inno/`、`bin/`、`obj/` 或 `TestResults/`。

## 公开发布门禁

1. 在安装项目要求的 .NET SDK 后完成干净 restore/build/test/publish；
2. Windows 10 x64 安装、升级、卸载和托盘 smoke；
3. Windows 11 稳定渠道复验；
4. 100/125/150/200% DPI 与多显示器定位矩阵；
5. 组织控制的 Authenticode 或 Trusted Signing 与 provenance；
6. 单独安排的长期稳定性测试；
7. 辅助功能和高对比度复审。

## 推荐下一任务

在具备 `.NET SDK 10.0.302` 的环境执行干净构建、测试和 publish；随后进行 Windows 10/11、DPI 与多显示器发布矩阵。不要自动发布未签名产物。
