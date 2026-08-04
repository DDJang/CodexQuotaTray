# Rust/Win32 归档说明

## 归档位置

迁移前最后一个完整且通过本地验证的 Rust/Win32 实现保存在 annotated tag：

```text
archive/rust-win32-final
```

该 tag 指向：

```text
dd099c4decf20adea3f7d7e5aa09837857c6b658
```

归档说明明确指出该提交不一定曾作为公开版本发布。已有正式发布 tag `v0.1.4` 继续指向 `22c19e4b0e1809d98dc304f4ce76f95072d8e42c`，不应移动或重写。

## 当前维护策略

- WinUI 3 是当前唯一开发、测试和打包入口。
- 当前分支不保留 `old/`、`legacy/` 或 `backup/` 形式的旧项目副本。
- 不维护长期 `legacy/rust-win32` 分支。
- 如未来确需修复旧版，应从 `archive/rust-win32-final` 创建临时维护分支。
- 旧 Rust ZIP 安装/卸载脚本不迁移到 WinUI 便携 ZIP。

## 恢复归档

建议在独立 worktree 中查看或验证，不切换当前工作区：

```powershell
git worktree add <temporary-path> archive/rust-win32-final
```

归档提交包含当时的 Cargo 清单、Rust/Win32 源码、匿名 fixture、测试、资源和旧打包脚本。其历史验证结果不等同于 WinUI 当前版本的发布验证，也不能证明该提交曾公开发布。

## 协议资料

`schemas/` 当前只保留 `README.md`、`CODEX_VERSION` 和 `codex-0.144.5/` 这一套版本化完整协议基线，作为 WinUI 的协议基线、兼容性审计和测试参考。WinUI 当前运行时和项目文件不直接加载这些 schema；历史重复生成结果已从工作树清理，历史由 Git 保存。协议升级时应以新的完整版本化目录整体替换旧目录，不在当前分支长期并存多个历史版本。

迁移过程的逐次实施记录和旧架构细节继续由 Git 历史及本归档 tag 保存，当前分支不维护重复的执行流水账或迁移阶段文档。
