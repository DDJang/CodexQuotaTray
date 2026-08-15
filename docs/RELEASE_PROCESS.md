# CodexQuotaTray GitHub 发布状态机

本文件定义“一句话 GitHub 发布流程”的执行顺序。平台 tag、产物、签名和 update-manifest 合同仍以 [RELEASE](RELEASE.md)、两个 Release workflow 和 `.github/scripts/update-release-manifest.ps1` 为准。

## 触发与授权

只有在用户明确说：

```text
进入 GitHub 发布流程，版本号 X.Y.Z
```

时，才执行本流程。该表述授权完成版本修改、release notes、commit、push、PR 创建或复用、等待 CI、merge、平台 tag、Release workflow 监控和发布结果验证。没有这句明确授权时，普通开发、测试或审查任务不自动进入发布流程。

实际执行由 `scripts/publish-release.ps1 -Version X.Y.Z` 驱动；Codex 在调用脚本前负责分析代码变化，并分别生成：

- `android/release-notes/X.Y.Z.md`
- `windows/release-notes/X.Y.Z.md`

脚本只校验和使用这两份文件，不根据 commit message 猜测用户更新内容。

## 状态机

### 1. Preflight

脚本首先严格验证 `MAJOR.MINOR.PATCH` 版本，确认当前是 Git 仓库中的非 detached、非 `main` 分支，检查工作区和明显敏感文件，检查 `gh` 可用且已认证，确认 `origin/main` 存在并检查当前 HEAD 已包含它，同时检查 notes 路径和目标 tag 冲突。正式运行会 fetch；DryRun 只使用不会更新 refs 的检查。

### 2. 选择上一平台 Release tag

Android 和 Windows 独立选择上一版本：

```text
android-vX.Y.Z
windows-vX.Y.Z
```

候选 tag 必须符合严格版本格式，且 tag commit 是当前待发布 HEAD 的祖先；在满足条件的候选中按语义版本选择最高者。不能只按字符串排序，也不能把另一个平台的 tag 当作上一版本。目标版本必须高于各自上一平台版本。

### 3. Release notes

Codex 以“上一 Android tag → 当前 HEAD”和“上一 Windows tag → 当前 HEAD”为边界，筛选对应平台用户可感知的变化，生成两份独立 Markdown。只保留新增、优化、修复等用户能理解的内容，不写 commit hash、作者、PR 编号、完整 changelog 或普通测试/重构细节。

脚本在版本修改和构建前检查文件存在且非空；正式流程缺失时以非零状态停止，例如：

```text
Missing Android release notes for version X.Y.Z
Missing Windows release notes for version X.Y.Z
```

### 4. 更新版本

正式运行时只修改：

- `android/app/build.gradle.kts`：`versionName = X.Y.Z`；`versionCode` 使用当前值和历史 Android Release tag 中最大值计算，确保新值严格更大；
- `windows/src/CodexQuotaTray.App/CodexQuotaTray.App.csproj`：`<Version>X.Y.Z</Version>`。

若文件已经是目标版本，脚本保持它；若 Android `versionCode` 已经高于历史最大值，也不会重复递增。不修改其它版本引用、依赖、签名或应用身份。

### 5. 本地验证

版本更新后运行：

```text
android\gradlew.bat :app:testDebugUnitTest :app:lintDebug :app:assembleDebug
pwsh -NoProfile -File .\windows\scripts\verify-winui.ps1 -Mode Release
pwsh -NoProfile -File .\.github\scripts\test-update-release-manifest.ps1
git diff --check
```

任何失败都停止，不跳过检查、不修改测试、不自动进行危险恢复。

### 6. Commit + push

验证成功后检查差异和敏感文件，使用 `release: prepare X.Y.Z` 提交当前发布准备修改，然后只 push 当前分支到 `origin`，不 force push。

### 7. 创建或复用 PR

脚本按当前分支和 `main` 查找 open PR；找到就复用，否则创建一个 PR，不创建重复 PR。脚本读取近期已合并 PR 和 merge commit 的父节点确认 merge convention；当前仓库近期记录显示采用 squash merge，无法可靠识别时停止而不是猜测。

### 8. 等待 PR CI 与 merge

脚本使用 GitHub CLI 查询 PR checks 的真实状态，直到相关检查全部成功；失败、取消或错误立即停止，不执行 merge。CI 全通过后按已识别的 convention merge 到 `main`。

### 9. 等待精确的 main CI

merge 后 fetch `origin/main`，取得 merge 后实际的 `origin/main` HEAD SHA。然后只监控该 SHA 对应的 `Android CI` (`android-ci.yml`)、`Windows CI` (`windows-ci.yml`) 及当前仓库发布门禁要求的其它 main push checks。找不到对应 run、run 失败、取消或超时，都不进入 tag 阶段。PR CI 成功不能替代 merge 后的 main CI。

### 10. 创建并 push 两个平台 tag

main CI 全部成功后，确认 `android-vX.Y.Z` 和 `windows-vX.Y.Z` 均不存在，或已正确指向同一个已验证的 main SHA。当前仓库 `android-v0.8.0` 和 `windows-v0.8.0` 是 annotated tag，因此默认创建 annotated tags。两个 tag 先指向同一 SHA，再优先使用一次 atomic push：

```text
git push --atomic origin refs/tags/android-vX.Y.Z refs/tags/windows-vX.Y.Z
```

已有 tag 指向其它 SHA 时直接失败，不移动、删除或 force push。

### 11. 监控 Release workflow

tag push 后，脚本按精确 tag、精确 SHA 和 workflow 文件查找并等待 `Android Release` (`android-release.yml`) 与 `Windows Release` (`windows-release.yml`)。两者必须进入 completed/success；queued 或 in_progress 不能提前视为成功；任一失败立即停止，不自动删除 tag 或重发。

两个 Release workflow 共用 `update-manifest-publish` concurrency group，且 `cancel-in-progress: false`，因此各自完成 Release 和资产后，对统一 manifest 的 read-modify-write 不会互相取消。

### 12. 验证 Release 与 update-manifest

Android Release 必须存在 APK 和 `SHA256SUMS.txt`；Windows Release 必须存在 ZIP、setup.exe 和 `SHA256SUMS.txt`。Release body 必须与对应 platform notes 文件一致，并且来自 workflow 的 `--notes-file`，不是 `--generate-notes`。

最后读取 `update-manifest` 分支上的真实 `update-manifest.json`，验证 schemaVersion、平台版本、tag、asset URL、SHA256、size 和 release notes。只有 Android Release、Windows Release 和两个平台的 manifest 节点都成功更新，才报告发布成功。

## DryRun

```powershell
.\scripts\publish-release.ps1 -Version 0.8.1 -DryRun
```

DryRun 会真实读取并报告当前 branch、HEAD、main 关系、工作区、`gh` 认证状态、远端仓库、上一 Android/Windows ancestor tag、目标版本、notes 路径、目标 tag 冲突、预期版本和 Android versionCode、将运行的本地验证、将复用/创建的 PR、等待的 CI、待创建的两个 tag、Release 和 manifest 验证目标。

DryRun 不执行版本写入、fetch 写 refs、构建、commit、push、PR create、merge、tag、Release 或 manifest 写入。release notes 缺失会明确显示为正式运行的阻塞项，但不会为了展示计划而修改工作区。

## 失败与重跑

任何关键步骤以非零状态退出，并保留当前状态供人工处理。脚本不会自动 reset、restore、stash、删除/移动 tag、删除 Release 或绕过 CI。已存在且正确完成的 PR 可以复用；已存在但指向错误 SHA 的 tag 直接失败。已存在的正确 tags、Release 或 manifest 状态只在脚本能够验证完整一致时识别为已完成，否则停止并报告。
