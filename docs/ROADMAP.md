# Windows Roadmap

## Current

- 保持额度/统计双页、通知合并、托盘交互、缓存和 Dev/Production 隔离稳定。
- 保持完整离线测试、安装/升级路径和发布 workflow 可重复。

## Next

- 根据真实问题扩展 Windows 10/11、DPI、多显示器、高对比度和辅助功能验证。
- 继续观察 Explorer 重启、睡眠/网络恢复和长期运行资源占用。
- 自动更新已经落地；后续继续验证其稳定性、升级路径、失败恢复和 Release 集成。
- 代码签名仍是待完成的接入事项；现有 Windows Release 不因该事项而被描述为已签名。

## Non-goals

- Electron/WebView、网页抓取或云端账户后端；
- Codex 聊天、项目管理或账户写操作；
- 用 Preview/Dev 覆盖 Production 身份或数据。

## Validation

日常变更按 [WinUI README](../windows/README.md) 使用 Quick/Full；正式流程见
[RELEASE](RELEASE.md)。
