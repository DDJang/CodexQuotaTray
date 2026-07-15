# P0 App Server 可行性记录

## 环境

- Codex CLI：`codex-cli 0.137.0`（npm 安装）。
- App Server transport：默认 `stdio://`，newline-delimited JSON。
- Schema：使用稳定生成器输出，未启用 `--experimental`。
- 平台：Windows x86_64。

## 脱敏响应形状

以下数值和时间戳经过人工调整，只保留已观察到的字段结构。

`account/read`：

```json
{
  "account": {
    "type": "chatgpt",
    "email": "[REDACTED]",
    "planType": "plus"
  },
  "requiresOpenaiAuth": true
}
```

程序的 typed model 不声明 `email`，不会把它传入摘要或日志。

`account/rateLimits/read`：

```json
{
  "rateLimits": {
    "limitId": "codex",
    "limitName": null,
    "primary": {
      "usedPercent": 28,
      "windowDurationMins": 10080,
      "resetsAt": 1893456000
    },
    "secondary": null,
    "credits": {
      "hasCredits": false,
      "unlimited": false,
      "balance": "0"
    },
    "planType": "plus",
    "rateLimitReachedType": null
  },
  "rateLimitsByLimitId": {
    "codex": "<same snapshot shape>"
  }
}
```

`account/rateLimits/updated` 的 `params.rateLimits` 使用同一快照类型，但可能仅包含发生变化的字段。

## PRD 偏差

1. 0.137.0 的稳定与实验 schema 均没有 `rateLimitResetCredits.availableCount`，也没有 reset-credit consume 方法。
2. `credits { hasCredits, unlimited, balance }` 是通用 credits 状态，不能解释为可用重置次数。
3. 实际探测中 10080 分钟窗口位于 `primary` 且 `secondary` 为空，因此不得把 `primary` 固定解释为 5 小时。
4. 响应可能同时提供 legacy `rateLimits` 和 `rateLimitsByLimitId`；实现优先采用非空的多 bucket 视图。
5. 更新通知是稀疏数据，不能直接覆盖完整快照。

## 安全结论

P0 只发送 `initialize`、`initialized`、`account/read` 和 `account/rateLimits/read`。没有任何账户写操作，不会消耗 rate-limit reset credit。

## 实施验证结果

2026-07-15 在上述环境完成以下验证：

- `cargo fmt --all`：通过。
- `cargo check --all-targets`：通过。
- `cargo clippy --all-targets --all-features -- -D warnings`：通过，零警告。
- `cargo test --all-targets`：10 个 fixture/parser 测试全部通过；测试过程不启动 App Server。
- `cargo run -- --watch-seconds 10`：成功读取 ChatGPT 套餐和单个 10080 分钟额度窗口。
- 监听窗口内未观察到 `account/rateLimits/updated`；通知解析和稀疏合并由本地 fixture 覆盖。
- 关闭 stdin 后 App Server 自然退出，无强制终止，命令退出码为 0。
- 运行后未发现本次 spike 遗留的 App Server；系统中已有的两个长期进程分别由 ChatGPT 桌面应用和 VS Code 在本次运行前启动。

真实额度百分比和重置时间未写入本文档或 fixture。
