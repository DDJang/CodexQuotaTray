# App Server schemas

[`CODEX_VERSION`](CODEX_VERSION) 是仓库中 Codex CLI 协议基线版本的唯一事实源；对应生成文件
位于 `codex-<version>/`。运行时不读取本目录，schema 仅用于协议升级审计、DTO 校准和匿名测试。

升级时由维护者显式修改 `CODEX_VERSION`，再从仓库根目录生成：

```powershell
$version = ((Get-Content .\schemas\CODEX_VERSION -Raw).Trim() -replace '^codex-cli\s+', '')
npx --yes "@openai/codex@$version" app-server generate-json-schema `
  --out ".\schemas\codex-$version"
```

生成时不使用 `--experimental`，也不手工编辑生成 JSON。升级后必须同步审查
[API_CONTRACT](../docs/API_CONTRACT.md)、DTO、fixture 和完整离线测试。审查完成后必须删除
`schemas/` 下所有旧的 `codex-<version>/` 目录，只保留 `CODEX_VERSION` 指向的当前 schema，
并通过 `git status` 确认旧版本删除已纳入本次变更。
