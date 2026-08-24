# CodexQuotaTray GitHub 发布状态机

本文件定义“一句话 GitHub 发布流程”的执行顺序。平台 tag、产物、签名和
`update-manifest` 合同仍以 [RELEASE](RELEASE.md)、对应 Release workflow 和
`.github/scripts/update-release-manifest.ps1` 为准。

## 触发与平台选择

只有用户明确说明平台和版本时，才执行本流程，例如：

```text
进入 GitHub 发布流程，平台 Windows，版本号 X.Y.Z
进入 GitHub 发布流程，平台 Android，版本号 X.Y.Z
进入 GitHub 发布流程，平台 All，版本号 X.Y.Z
```

这类明确表述授权完成选定平台的版本修改、release notes、commit、push、PR 创建或复用、等待 PR CI、
merge、tag、Release workflow 监控和发布结果验证。没有明确授权时，普通开发、测试或审查任务不自动进入发布流程。

### 发布 PR 合并策略

所有平台发布 preparation PR 统一使用 **squash merge** 合并到 `main`；不得根据近期历史推断
普通 merge，也不得使用 rebase merge。自动化发布脚本必须直接请求 squash merge，并在后续
post-merge resume 中校验 merged PR 的 `headRefOid` 与 `mergeCommit` 身份。仓库历史中既有的
普通 merge 不改变本规则。

实际执行由 `scripts/publish-release.ps1` 驱动，`-Platform` 是必填参数：

```powershell
.\scripts\publish-release.ps1 -Platform Windows -Version X.Y.Z
.\scripts\publish-release.ps1 -Platform Android -Version X.Y.Z
.\scripts\publish-release.ps1 -Platform All -Version X.Y.Z
```

`Windows` 和 `Android` 只处理选定平台；`All` 才是确实需要双平台同步准备和发布时的入口。脚本没有隐式的双平台默认值。
Codex 在调用脚本前只为选定平台准备对应 notes，并将 notes 写入当前 branch 的提交；脚本启动时工作区必须 clean，因此不能把未提交的 notes 留在工作区等待脚本创建提交：

- Windows：`windows/release-notes/X.Y.Z.md`
- Android：`android/release-notes/X.Y.Z.md`
- All：两份文件都必须存在且非空

## 隔离合同

| 选择 | 版本文件 | release notes | 本地验证 | tag 与 Release | manifest 验证 |
| --- | --- | --- | --- | --- | --- |
| Windows | Windows App `Version` | Windows notes | Windows 验证 | `windows-vX.Y.Z` 与 Windows Release | 仅 `windows` 节点 |
| Android | Android `versionName`/`versionCode` | Android notes | Android 验证 | `android-vX.Y.Z` 与 Android Release | 仅 `android` 节点 |
| All | 两个平台 | 两份 notes | 两个平台 | 两个平台，tag 原子 push | 两个节点 |

未选定平台的版本文件、notes、目标 tag、Release workflow、Release 和 manifest 节点都不会被脚本读取为本次发布目标、修改或强制验证。`All` 才会恢复原来的双平台状态机。

## 状态机

### 1. Preflight

脚本首先严格验证 `MAJOR.MINOR.PATCH` 版本和 `Windows|Android|All` 平台，确认当前是 Git 仓库中的非 detached、非 `main` 分支，要求工作区 clean 并检查明显敏感文件，检查 `gh` 可用且已认证，确认 `origin/main` 存在并检查当前 HEAD 已包含它；如果发现当前 release preparation 已经安全地 squash/merge 到 main，则改为验证该 post-merge resume 状态。两种模式都只检查选定平台的 notes、版本文件和目标 tag 冲突。正式运行会 fetch；DryRun 只使用不会更新 refs 的检查。

### 2. 选择上一平台 Release tag

选定平台独立选择上一版本：

```text
Windows -> windows-vX.Y.Z
Android -> android-vX.Y.Z
All     -> 两者都选择
```

候选 tag 必须符合严格版本格式，且 tag commit 是当前待发布 HEAD 的祖先；在满足条件的候选中按语义版本选择最高者。不能只按字符串排序，也不能把另一个平台的 tag 当作上一版本。目标版本必须高于每个选定平台的上一版本。

### 3. Release notes

Codex 以选定平台的上一 tag → 当前 HEAD 为边界，筛选该平台用户可感知的变化，生成对应 Markdown，并在调用脚本前提交 notes。只保留新增、优化、修复等用户能理解的内容，不写 commit hash、作者、PR 编号、完整 changelog 或普通测试/重构细节。

脚本在版本修改和构建前只检查选定平台的文件存在且非空。Windows-only 不要求 Android notes；Android-only 不要求 Windows notes；All 两者都要求。

### 4. 更新版本

正式运行时只修改选定平台的版本文件：

- Android：`android/app/build.gradle.kts` 的 `versionName = X.Y.Z`；`versionCode` 使用当前值和历史 Android Release tag 中最大值计算，确保新值严格更大；
- Windows：`windows/src/CodexQuotaTray.App/CodexQuotaTray.App.csproj` 的 `<Version>X.Y.Z</Version>`。

若文件已经是目标版本，脚本保持它；不修改未选平台版本、其它版本引用、依赖、签名或应用身份。

### 5. 选定平台的本地验证

Windows-only 运行 Windows Release 验证；Android-only 运行 Android 测试、lint 和 Debug assemble；All 运行两者。两种选择都会运行共同的离线 `update-release-manifest` writer 测试和 `git diff --check`：

```text
Windows: pwsh -NoProfile -File .\windows\scripts\verify-winui.ps1 -Mode Release
Android: android\gradlew.bat -p android :app:testDebugUnitTest :app:lintDebug :app:assembleDebug
Common:  pwsh -NoProfile -File .\.github\scripts\test-update-release-manifest.ps1
         pwsh -NoProfile -File .\.github\scripts\test-publish-release.ps1
         git diff --check
```

任何失败都停止，不跳过检查、不修改测试、不自动进行危险恢复。

### 6. Commit + push

验证成功后检查差异和敏感文件，只 stage 选定平台的版本文件；notes 已属于 clean HEAD，不在本次脚本工作区中新增或修改。脚本使用带平台范围的 `release: prepare ...` 提交，然后只 push 当前分支到 `origin`，不 force push。

如果脚本在 release preparation commit 已经存在后重跑，只有在当前 HEAD 的提交 subject、选定平台版本文件、选定平台 notes、提交改动范围和 clean worktree 都与本次目标一致时，才会跳过 commit（不创建空 commit），push 当前分支并继续查询/复用已有 PR；状态不完整时仍会停止。

如果 release preparation PR 已经 squash/merge 到 `main`，脚本必须同时验证 main 历史中的对应 squash commit，或包含当前 release branch tip 的 merge commit，并确认选定平台版本和 notes 与当前准备状态一致。squash 的身份优先由 GitHub merged PR 元数据确认：merged PR 的 `headRefOid` 必须精确等于当前 release branch HEAD，`mergeCommit` 必须属于 `origin/main` 历史；squash commit 可以同时包含本次产品代码和测试改动，不要求 changed paths 只包含 release metadata。普通 merge 继续使用 parent/ancestry 校验。验证通过后跳过 preparation、PR 和 PR CI，使用已确认的 release main commit 继续 tag、Release workflow 和 `update-manifest`；无法唯一确认时停止，不因 `origin/main` 前进而放宽 ancestry 或 SHA 校验。

### 7. 创建或复用 PR

普通 release preparation 模式下，脚本按当前分支和 `main` 查找 open PR；找到就复用，否则创建一个 PR，不创建重复 PR。PR 标题和正文标明本次平台范围。post-merge resume 模式跳过本节。

### 8. 等待 PR CI 与 merge

PR CI 是合并前验证。脚本使用 GitHub CLI 查询 PR checks 的真实状态，直到相关检查全部成功；失败、取消或错误立即停止，不执行 merge。PR CI 全通过后使用 squash merge 合并到 `main`。

普通 CI 只由 PR 和显式 `workflow_dispatch` 触发；merge 到 `main` 不会再次触发重复的普通 CI。

### 9. 确认实际 main commit

merge 后 fetch `origin/main`，取得 merge 后实际的 `origin/main` HEAD SHA。普通模式确认 squash/merge commit 已属于 `origin/main` 历史；post-merge resume 模式则重新确认已验证的 release main commit 仍属于 `origin/main`。随后确认选定平台的版本文件和 release notes 与本次准备状态一致。无法确认 commit、版本或 notes 时停止，不进入 tag 阶段。

这里不再等待独立的 merge 后 CI：PR CI 负责合并前验证，Release workflow 负责正式发布验证。

### 10. 创建并 push 选定平台 tag

确认实际的 main SHA、版本和 notes 后，只确认选定平台的目标 tag 不存在，或已正确指向同一个已验证的 main SHA。当前仓库平台 tag 是 annotated tag，因此默认创建 annotated tag；All 会先准备两个 tag，再优先使用一次 atomic push：

```text
Windows: git push --atomic origin refs/tags/windows-vX.Y.Z
Android: git push --atomic origin refs/tags/android-vX.Y.Z
All:     git push --atomic origin refs/tags/android-vX.Y.Z refs/tags/windows-vX.Y.Z
```

已存在 tag 指向其它 SHA 时直接失败，不移动、删除或 force push。

### 11. 监控选定平台 Release workflow

tag push 后，脚本按精确 tag、精确 SHA 和 workflow 文件查找并等待选定平台的 Release workflow。workflow 必须进入 `completed/success`；queued 或 `in_progress` 不能提前视为成功；任一失败立即停止，不自动删除 tag 或重发。

Release workflow 仍共用 `update-manifest-publish` concurrency group，且 `cancel-in-progress: false`。每个 workflow 完成自己的 Release 和资产后，继续通过现有 read-modify-write 流程只替换当前平台的 `update-manifest` 节点；本次脚本改动不改变该协议或语义。

### 12. 验证选定平台 Release 与 update-manifest

脚本只读取和验证选定平台的 Release、资产、notes body、checksum、size 和 manifest 节点：

- Android：APK、`SHA256SUMS.txt`、Android Release body 和 `android` 节点；
- Windows：ZIP、setup.exe、`SHA256SUMS.txt`、Windows Release body 和 `windows` 节点；
- All：两套都验证。

只有本次选定平台的 Release 和对应 manifest 节点都成功，才报告发布成功。未选定平台的既有 manifest 节点会被保留，不因本次独立发布而被重写或要求同步到新版本。

## DryRun

```powershell
.\scripts\publish-release.ps1 -Platform Windows -Version 0.8.8 -DryRun
.\scripts\publish-release.ps1 -Platform Android -Version 0.8.8 -DryRun
.\scripts\publish-release.ps1 -Platform All -Version 0.8.8 -DryRun
```

DryRun 会真实读取并报告当前 branch、HEAD、`main` 关系、工作区、`gh` 认证状态、远端仓库、选定平台的 ancestor tag、目标版本、notes 路径、目标 tag 冲突、预期版本和 Android `versionCode`（如适用）、将运行的本地验证、后续 PR CI、merge、tag、Release workflow 和 manifest 阶段的计划。DryRun 在查询或创建 PR 前退出，不会报告具体 PR 的复用或创建结果。

DryRun 不执行版本写入、fetch 写 refs、构建、commit、push、PR 查询或 create、merge、tag、Release 或 manifest 写入。选定平台的 release notes 缺失会明确显示为正式运行的阻塞项，但不会为了展示计划而修改工作区。

## 失败与重跑

任何关键步骤以非零状态退出，并保留当前状态供人工处理。脚本不会自动 reset、restore、stash、删除/移动 tag、删除 Release 或绕过 CI。已存在且正确完成的 PR 可以复用；已存在但指向错误 SHA 的 tag 直接失败。已存在的正确 tag、Release 或选定平台 manifest 状态只在脚本能够验证完整一致时识别为已完成，否则停止并报告。
