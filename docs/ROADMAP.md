# CodexQuotaTray Roadmap

## 已完成的能力

- C# + WinUI 3 已成为当前正式开发、验证和发布入口；Rust/Win32 仅保留归档。
- 受控 App Server 子进程、UTF-8 JSONL transport、唯一请求 ID 路由、超时和进程树回收。
- Typed protocol DTO、动态额度窗口规范化、重置卡数量及已知到期摘要投影。
- 单 in-flight 刷新协调、定时和手动模式、服务端稀疏通知合并、通知溢出补读和失败保留。
- WinUI 托盘、动态额度面板、设置窗口、主题、通知和系统恢复事件。
- 设置、非敏感额度缓存和提醒防重复状态持久化。
- 50%、20%、10% 阈值提醒和额度周期重置提醒。
- Production 与 Preview 单实例、托盘和数据身份隔离；Demo 使用 Preview 身份。
- Preview/Demo 开机启动能力隔离，不会修改 Production 启动项。
- Demo Runtime、Live Preview 和 Production Live 启动模式。
- Fake App Server、匿名 fixture 和完整离线测试入口。
- 仓库 SDK 固定、统一 Quick/Full/Release 验证入口和显式 NuGet 配置。
- Folder-based self-contained x64 publish、便携 ZIP 脚本和 per-user Inno 安装器定义。
- 产品版本统一由 App 项目声明并通过程序集版本提供给 UI、诊断和协议初始化。

## 当前未完成的公开发布门禁

- Windows 10 x64 人工运行与交互验收。
- Windows 11 稳定渠道人工复验；预览渠道结果不能替代该门禁。
- 100%、125%、150%、175%、200% DPI 的布局和定位矩阵。
- 单显示器、多显示器、不同缩放组合及任务栏位置矩阵。
- 高对比度、系统透明效果关闭和辅助功能人工复审。
- Inno 安装、原位升级、卸载、启动项及 KeepUserData 的完整人工验证。
- 未签名、签名后 EXE、便携 ZIP 和安装器的完整产物验收。
- 组织控制的 Authenticode 或 Trusted Signing、独立校验和与 provenance。
- WinUI 版本的长期运行、Explorer 重启、睡眠恢复、网络恢复和资源稳定性验证。
- 真实 Codex 账户只读 smoke；该验证必须显式授权且不得保存原始响应。

在这些门禁完成并留下可审计证据前，不得将当前产物描述为已完成公开发布验证。

## 后续产品与工程任务

- 建立可重复的 Windows 10/11、DPI、多显示器和高对比度人工验收记录。
- 在受控环境验证安装、升级、卸载、数据保留和启动项生命周期。
- 接入组织控制的签名服务，并定义签名后重新生成 manifest、校验和和 provenance 的流程。
- 执行单独安排的长期稳定性测试，跟踪进程、句柄、内存、托盘恢复和 App Server 子进程回收。
- 协议基线升级时生成 schema diff，并仅针对当前只读子集更新 DTO、fixture 和回归测试。
- 根据实际无障碍审查结果修复键盘、焦点、读屏或对比度缺陷，不预先声明通过。

具体发布步骤见 [Release and packaging guide](RELEASE.md)。运行身份与技术边界见 [技术设计](TECH_DESIGN.md)。
