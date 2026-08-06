# CodexQuotaTray Android P0

这是个人使用的实验性 P0 验证，不是 Android Bridge、Android UI 或生产日志系统。
目标只有一个：确认真实 Android ARM64 + Termux 中的 Codex App Server 能否通过
stdin/stdout JSONL 完成初始化和额度读取。

脚本位置：`android/poc/p0_handshake.py`。

## Termux 基础软件

在 Termux 中准备 Python 3，以及 Codex CLI 所需的 Node/npm 运行时：

```sh
pkg update
pkg install python nodejs-lts
python3 --version
node --version
npm --version
```

本仓库不会自动安装 Codex，也不会猜测未验证的 Android 构建来源。请使用已经
确认支持 Android ARM64 的 Codex 构建，并记录其来源、版本、commit/tag 和 SHA-256。
先确认 Termux 本身是 ARM64，再按该构建来源的说明安装或解压 Codex；可以把可执行文件
放到 PATH，也可以在探针命令中传绝对路径：

```sh
uname -m
dpkg --print-architecture
command -v codex
codex --version
sha256sum "$(command -v codex)"
```

通常应看到 `aarch64` 或 `arm64`。如果来源没有提供 Android ARM64 构建，P0 应保持
blocked，不要用桌面二进制或模拟器结果替代。

## 手机本地登录

在同一个 Termux 用户环境中完成一次 Codex CLI 支持的本地登录。常见命令是：

```sh
codex login
```

按当前 CLI 的交互提示完成登录。认证只保留在 Termux 的本地配置中，不复制到
Android App、脚本参数或日志。登录后重新执行 `codex --version`，确认 CLI 仍可用。
如果当前 CLI 使用不同的登录入口，以该版本的 `codex --help` 为准，并在 P0 记录
实际命令。

## 精确运行命令

在仓库根目录执行。`--probe-account-read` 是可选探测；不支持时脚本会记录结果，
仍继续额度读取。

```sh
python3 android/poc/p0_handshake.py \
  --codex-bin "$(command -v codex)" \
  --probe-account-read \
  --timeout 30
```

完成构建来源记录后，可在同一命令末尾追加
`--codex-source "实际来源" --codex-revision "实际 commit 或 tag"`，让摘要同时保存
这些 provenance 字段。

认证持久化检查至少重新运行两次同一命令；每次脚本都会创建并清理新的 App Server
进程。脚本默认只输出一行 JSON 诊断摘要，不输出完整 stdout/stderr 或认证内容。

本机离线验证可以使用仓库内的 fake，但 fake 不能证明 Termux/ARM64 可行性：

```powershell
python -X utf8 android/poc/p0_handshake.py `
  --codex-bin python `
  --codex-arg android/poc/fake_codex.py `
  --probe-account-read `
  --timeout 10
```

要单独验证非法 JSON 诊断，可再传入 `--codex-arg=--malformed`；该次运行应以
`success=false` 和 `last_error=malformed_json` 结束。

## 成功输出示例

真实设备的数据、版本和路径会不同；下面只是结构化摘要格式示例：

```json
{
  "account_read_result": "unsupported",
  "app_server_started": true,
  "authenticated": true,
  "codex_sha256": "<sha256>",
  "codex_version": "<version>",
  "device_architecture": "aarch64",
  "initialize_succeeded": true,
  "last_error": null,
  "malformed_json_count": 0,
  "process_cleanup_succeeded": true,
  "quota_state": "available",
  "quota_window_count": 2,
  "rate_limits_read_result": "succeeded",
  "rate_limits_read_succeeded": true,
  "stderr_observed": false,
  "success": true
}
```

`platformFamily`、`platformOs` 为空不会单独使 P0 失败；额度窗口数量为零也可以是
有效结果，只要 `quota_state` 明确表达为 `zero_windows` 或 `unavailable`，而不是伪造
业务零值。

## 常见失败诊断

- `last_error= startup_failed`：检查 `command -v codex`、执行权限、Node/npm 和
  `codex --version`。
- `last_error= initialize_failed`：检查 App Server 版本、`--stdio` 参数和初始化
  响应；脚本不会打印原始错误正文。
- `last_error= unauthenticated` 或 `authenticated=false`：在同一个 Termux 用户中
  重新完成本地登录，确认认证配置没有被移动到其他用户或环境变量。
- `last_error= rate_limits_rpc_error`：查看 `rate_limits_rpc_code`，确认当前 CLI
  支持额度读取；不要把 RPC 错误正文复制到日志。
- `malformed_json_count>0`：App Server stdout 出现非法 JSONL；即使后续读取成功，也
  应记录为协议异常。
- `last_error=eof`：App Server 提前退出或 stdout 关闭，检查进程退出和 CLI 崩溃信息。
- `last_error=timeout`：进程未在总超时内返回；先重复运行，再检查 Termux 进程和网络。
- `process_cleanup_succeeded=false`：不要继续三次验收；先用 `ps` 检查残留的
  `codex app-server` 进程，并记录清理失败。
- `account_read_result=unsupported`：这是可选探测结果，不会单独阻止脚本继续读取
  额度；是否满足 Roadmap 的 P0 门禁需要单独记录协议决策。

只有真实 Android ARM64 手机上完成连续三次运行、认证持久化和无遗留子进程检查，
才能把 P0 标记为初步通过。本机 fake 验证和 Python 语法检查都不能替代真机结果。
