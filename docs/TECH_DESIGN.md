# CodexQuotaTray 技术设计

文档状态：WinUI 3 当前实现
协议基线：`codex-cli 0.144.5` stable App Server schema
最后更新：2026-07-29

## 1. 架构边界

当前应用由一个 WinUI 3 主进程和一个受控的 `codex app-server` 子进程组成。项目不使用 Electron、WebView UI、浏览器 Cookie 或网页抓取。

```text
CodexAppServerClient
        │ stdio JSONL
        ▼
JsonLineRpcConnection
        ▼
Protocol DTO / QuotaNormalizer
        ▼
QuotaRuntimeService / RefreshCoordinator
        ▼
AppUiState / MainViewModel
        ▼
WinUI window / tray / notifications
```

职责保持分离：

1. `Protocol/` 管理 App Server 进程、JSONL dispatcher、typed DTO 与额度规范化。
2. `Runtime/` 维护连接、刷新协调、稀疏通知合并和当前快照。
3. `Persistence/` 保存设置、非敏感缓存和提醒防重复状态。
4. `Presentation/` 只消费归一化状态，不解析原始 JSON。
5. `Alerts/` 负责阈值跨越与 at-most-once 状态。
6. WinUI App 负责窗口、托盘、系统事件、通知和平台操作。

旧 Rust/Win32 实现由 `archive/rust-win32-final` 保存，不参与当前构建或测试。

## 2. App Server 与协议

- Windows 依次查找 `codex.cmd`、`codex.exe` 和 `codex`；`--codex-bin` 可显式覆盖。
- 子进程使用 stdin/stdout UTF-8 JSONL，wire envelope 不要求 `jsonrpc` 字段。
- 初始化顺序为 `initialize`、`initialized`，随后只执行账户和额度读取。
- response 通过连接内唯一 ID 路由；通知通过独立路径交给 runtime。
- 请求有超时和取消；退出时先关闭 stdin 并等待正常结束，超时后才回收进程树。
- RPC 错误正文、stderr 原文和原始响应不写入日志或持久化。

运行时不直接加载 `schemas/**`。该目录暂时保留为协议生成基线、升级 diff 和兼容性审计资料。

## 3. 额度与状态

- 不固定解释 `primary` 或 `secondary`。
- 使用 `windowDurationMins`、`limitId` 和 `limitName` 识别额度窗口。
- `remainingPercent = clamp(100 - usedPercent, 0, 100)`。
- 缺失、null、未知和 malformed 数据不能静默变成零。
- `rateLimitResetCredits.availableCount` 是重置次数的权威字段；opaque ID 不进入 UI、日志或磁盘。
- 稀疏通知只覆盖明确存在的字段；无可靠基线时请求协调器补读。
- 刷新失败保留最后有效快照，并区分 refreshing、stale、offline、unauthenticated 和 unavailable。

所有 Startup、Manual、CardOpened、Resume、NetworkRestored、Scheduled 和服务端通知补读都进入同一个 `RefreshCoordinator`。任意时刻最多存在一个主动读取。

## 4. 持久化与隐私

正式数据目录为：

```text
%LOCALAPPDATA%\CodexQuotaTray
```

允许保存：

- 刷新与显示设置；
- 匿名额度数字缓存；
- 最后成功时间和非敏感版本信息；
- 伪匿名窗口键与提醒防重复状态。

禁止保存或记录：

- token、Cookie、邮箱、完整账户 ID；
- 原始 limit ID、reset-credit ID；
- App Server 原始认证或额度响应；
- `codexHome`、CLI 绝对路径、stderr 原文；
- 对话、项目代码或浏览历史。

JSON 文件限制大小并使用同目录临时文件原子替换。缓存恢复后先视为 stale，直到实时读取成功。

## 5. Windows UI

- App 使用 unpackaged、folder-based、self-contained WinUI 3。
- 托盘 callback、Explorer 重建广播和主窗口生命周期相互隔离。
- Explorer 无法确认托盘图标时保留可访问的任务栏降级入口。
- 主窗口使用 DWM 圆角以及 Acrylic → Mica → 不透明背景降级。
- 关闭窗口仅隐藏；显式退出或 `--shutdown-existing` 才结束进程。
- UI 只消费 `AppUiState` 和 view model，不接触 wire JSON。

## 6. 构建与发布

- `scripts/publish-winui.ps1` 生成 `target/winui-publish/`。
- `scripts/package-inno.ps1` 直接把该 publish 目录交给 `installer/CodexQuotaTray.iss`，不经过 ZIP。
- `scripts/package-winui.ps1` 可独立生成便携 ZIP，但它不是安装器中间产物。
- Inno 安装器使用 per-user 模式、固定 AppId、原位升级、正常关闭和 KeepUserData 语义。
- `dist/` 和 `dist-inno/` 都是被 Git 忽略的本地产物目录。

公开发布仍需要组织控制的签名、Windows 10/11 验证、DPI/多显示器矩阵和长期稳定性测试。

## 7. 测试

`winui/tests/` 使用 fake App Server 和匿名 fixture 覆盖：

- JSONL 路由、timeout、EOF、malformed response；
- missing/null/unknown quota window；
- 额度规范化和稀疏通知合并；
- refresh coordinator 和状态转换；
- settings/cache/alert persistence；
- 提醒阈值与跨重启去重；
- view model、主题、窗口和托盘策略。

常规测试不得要求真实 Codex 账户。真实资源 smoke 必须显式启用，且不得保存原始响应。
