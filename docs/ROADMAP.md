# CodexQuotaTray Windows Roadmap

这是一个个人维护的 0.x 项目。Roadmap 只记录近期方向；更完整的平台和兼容性检查会随着稳定版目标逐步补齐，不作为每个 Preview/Beta 的前置条件。

## Now

- 修复当前已知的 UI、托盘交互和额度刷新稳定性问题。
- 在常用 Windows 11 环境完成安装、启动、设置、托盘和卸载检查。
- 保持完整离线测试通过，并在每次 0.x 发布说明中列出已知限制。

## Next

- 完善安装器的原位升级、用户数据保留和开机启动体验。
- 根据实际问题扩展 Windows 10、DPI、多显示器和高对比度检查。
- 继续观察 Explorer 重启、睡眠恢复、网络恢复和长期运行资源占用。
- 根据真实反馈改进键盘、焦点、读屏和对比度。

## Later

- 在准备稳定版时评估代码签名。
- 需要升级 Codex CLI 协议基线时，更新 schema、DTO、fixture 和回归测试。
- 如果用户规模和维护需求增长，再增加更系统的兼容性矩阵与发布自动化。

当前发布步骤见 [构建与发布](RELEASE.md)，运行身份与技术边界见 [技术设计](TECH_DESIGN.md)。

个人使用的 Android 独立 APK 已完成 P0–P3；后续只规划后台自动刷新、通知、Widget
和开机启动，见 [Android Roadmap](ANDROID_ROADMAP.md)。
