# Windows ChatGPT / Codex 标题栏额度展示调研

- 范围：Windows；用户截图中 ChatGPT 应用 Codex 页面顶部、菜单与窗口按钮之间的空白区域。
- 本地基线：`ab84949e0f79268f53ee8050cfbf19f107a24fd4`。
- 调研分支：`codex/windows-chatgpt-quota-research`。
- 验证日期：2026-10-08（Asia/Shanghai）。
- 生命周期状态与实现 commit 以[调查索引](../README.md)为准；已实现原生覆盖窗并完成
  本机 owner/Z-order、后台可见性及静态性能验证，更广泛的交互与长期验证限制见下文。
- 调研结论基于下列固定 commit 的公开源码，没有安装或运行这些第三方项目。

## 结论

GitHub 已有直接对应标题栏目标的实现。最适合 CodexQuotaTray 的路线是：在自己的进程中创建
一个小型原生覆盖窗口，跟随 ChatGPT / Codex 主窗口，把现有额度投影画到标题栏空白处。
视觉上嵌入应用，实际仍是独立 HWND；无需修改宿主安装包、开启调试端口或解析宿主 DOM。

另一条路线确实把面板加入宿主内部 UI：冷启动宿主时开启 Chrome DevTools Protocol（CDP），
注入 JavaScript 创建 DOM。它更贴近宿主布局，但依赖内部结构和调试入口，且与本仓库
[PRD](../../PRD.md) 的网页 DOM 读取边界不一致，因此本次不推荐采用。

截图里的位置属于自定义应用顶部区域，不能仅凭截图把标准 Windows caption 高度当作准确边界。
尚未证明现有开源项目在用户当前 ChatGPT 版本上无需适配即可工作。

## 相似项目与实现证据

| 项目 | 实际展示方式 | 数据来源 | 对本仓库的价值 |
| --- | --- | --- | --- |
| [usage-indicator-for-codex](https://github.com/mursyidd/usage-indicator-for-codex) | WPF 透明覆盖窗口，标题栏居中 | 独立 Codex CLI App Server | 最接近红圈目标；参考跟随、owner、DPI 与不抢焦点 |
| [codex-usage-remaining](https://github.com/zoeyliew192/codex-usage-remaining) | 原生 WinForms 透明覆盖窗口，标题栏右侧 | 只读 HTTPS；本地额度日志 fallback | 参考事件合并与窗口层级；不移植其凭据、数据库读取路径 |
| [codex-token-overlay](https://github.com/soleillevant0125/codex-token-overlay) | 原生 WinForms 胶囊，支持标题栏与手动锚定 | 本地会话 Token；内部 IPC 跟随任务 | 参考窄窗口退化、按钮避让和多窗口识别；它显示 Token，不是套餐额度 |
| [codex-quota](https://github.com/keaipiao/codex-quota) | CDP 注入宿主侧栏底部面板 | 官方本地 App Server | 可解释真正内部面板的实现与维护成本，不作为本仓库实现路线 |

### 1. usage-indicator-for-codex：标题栏居中的独立窗口

审阅 commit：`e7fcb6d68a3e823e02ae9a9a6fd614740a5762c1`。

- [CodexWindowTracker.cs](https://github.com/mursyidd/usage-indicator-for-codex/blob/e7fcb6d68a3e823e02ae9a9a6fd614740a5762c1/src/UsageIndicatorForCodex/Services/CodexWindowTracker.cs)
  使用 `SetWinEventHook(..., WINEVENT_OUTOFCONTEXT)` 跟踪前台、位置、最小化、销毁等事件。
  以进程名、Store package family、主窗口标题和 tool-window 排除条件共同判断目标；
  不只靠标题字符串。失去前台时允许继续跟随此前有效宿主。
- [UsageOverlayWindow.cs](https://github.com/mursyidd/usage-indicator-for-codex/blob/e7fcb6d68a3e823e02ae9a9a6fd614740a5762c1/src/UsageIndicatorForCodex/Views/UsageOverlayWindow.cs)
  创建无边框透明 WPF Window，关闭 taskbar、激活与焦点；通过 `WindowInteropHelper.Owner`
  绑定宿主窗口，使用 `WS_EX_TOOLWINDOW`、`WS_EX_NOACTIVATE`，按是否可点击切换
  `WS_EX_TRANSPARENT`。`CalculatePlacement` 用 `GetDpiForWindow` 换算 DIP，
  横向居中并加用户 offset，纵向使用顶部 offset。
- [IndicatorCoordinator.cs](https://github.com/mursyidd/usage-indicator-for-codex/blob/e7fcb6d68a3e823e02ae9a9a6fd614740a5762c1/src/UsageIndicatorForCodex/Services/IndicatorCoordinator.cs)
  分开处理窗口几何变化与额度刷新，移动时不触发额度请求，最小化时隐藏。
- [CodexAppServerUsageProvider.cs](https://github.com/mursyidd/usage-indicator-for-codex/blob/e7fcb6d68a3e823e02ae9a9a6fd614740a5762c1/src/UsageIndicatorForCodex/Services/CodexAppServerUsageProvider.cs)
  启动本地 App Server，发送 `initialize`、账户读取和 `account/rateLimits/read`。
  [README](https://github.com/mursyidd/usage-indicator-for-codex/blob/e7fcb6d68a3e823e02ae9a9a6fd614740a5762c1/README.md)
  明确说明额度属于 CLI 登录账户，Desktop 只是定位目标；两者账户不同会显示不同额度。

可借鉴窗口控制结构，但不应直接照搬其 WPF 工程、仅 Windows 11 的支持声明或固定窗口筛选。
它的居中公式没有解决用户截图中左侧菜单宽度、宿主缩放和窄窗口的全部避让问题。

### 2. codex-usage-remaining：原生透明窗与宿主 owner

审阅 commit：`cbf75608cdbb19a1ec26b1afdbbafc269efae9f9`。

[CodexUsageRemaining.cs](https://github.com/zoeyliew192/codex-usage-remaining/blob/cbf75608cdbb19a1ec26b1afdbbafc269efae9f9/CodexUsageRemaining.cs)
中的 `QuotaOverlayForm`：

- 使用无边框 WinForms Form、透明色、`TopMost = false` 和不激活显示。
- 监听 foreground、move/size、location-change；用 `Interlocked` 合并回调后提交 UI 线程。
- `PositionForWindow` 优先读 DWM extended frame bounds，失败退回 `GetWindowRect`。
  按 DPI 缩放，右侧保留固定宽度给窗口按钮，再用文字测量决定覆盖窗宽度。
- 通过修改自己窗口的 `GWLP_HWNDPARENT` 设置宿主为 owner；没有把窗体作为宿主 child
  嵌入，也没有给宿主打补丁。`SetWindowPos` 使用不激活和保留 Z-order 的标志。
- `QuotaReader` 直接读 CLI auth 文件，调用 usage HTTPS，并以本地 SQLite 额度记录兜底。

该实现只按 `ChatGPT` 进程名识别窗口，筛选比较宽；固定按钮预留宽度也不能保证所有宿主
布局都适配。它与本仓库的额度认证、来源和缓存结构不同，应只借鉴覆盖窗机制。

### 3. codex-token-overlay：布局退化与锚定

审阅 commit：`b3a38d727fb2e0cf8e8c92ffff3f65da9a592dc5`。

- [CodexWindowLocator.cs](https://github.com/soleillevant0125/codex-token-overlay/blob/b3a38d727fb2e0cf8e8c92ffff3f65da9a592dc5/src/CodexTokenOverlay/CodexWindowLocator.cs)
  区分同一进程内的不同窗口候选，复核已识别 HWND/PID，读 DWM frame、caption-button
  bounds、显示器工作区和 DPI。
- [OverlayLayout.cs](https://github.com/soleillevant0125/codex-token-overlay/blob/b3a38d727fb2e0cf8e8c92ffff3f65da9a592dc5/src/CodexTokenOverlay/OverlayLayout.cs)
  的 `CalculateTitleBar` 把按钮、工作区、标题区和左侧预留纳入可用空间，
  寻找能完整放入标题栏的最大缩放，退化到单指标或隐藏。
- [TokenStripForm.cs](https://github.com/soleillevant0125/codex-token-overlay/blob/b3a38d727fb2e0cf8e8c92ffff3f65da9a592dc5/src/CodexTokenOverlay/TokenStripForm.cs)
  在普通模式使用 `WS_EX_NOACTIVATE` 与 `WM_MOUSEACTIVATE` 不激活处理，编辑模式允许调整。
- [README](https://github.com/soleillevant0125/codex-token-overlay/blob/b3a38d727fb2e0cf8e8c92ffff3f65da9a592dc5/README.md)
  描述主窗口八个参考点的手动附着；只在识别的宿主前台时显示。

可以借鉴布局与窗口筛选；套餐额度无需任务级定位，因此不需要引入该项目内部 IPC 或会话扫描。

### 4. codex-quota：真正加入应用 DOM 的侧栏面板

审阅 commit：`54e3e15c898b65d66db4ecffec1725c4d8e9275d`。

- [codex-cdp.ps1](https://github.com/keaipiao/codex-quota/blob/54e3e15c898b65d66db4ecffec1725c4d8e9275d/windows/codex-cdp.ps1)
  用 Store activation 启动宿主，并传入 loopback remote-debugging address/port；
  项目提供专用启动入口，普通已启动且未开启 CDP 的宿主不能直接附着。
- [watcher.mjs](https://github.com/keaipiao/codex-quota/blob/54e3e15c898b65d66db4ecffec1725c4d8e9275d/src/cdp/watcher.mjs)
  查验监听进程与 browser identity，通过 `Runtime.evaluate` 注入、更新 UI，
  使用 `Page.addScriptToEvaluateOnNewDocument` 注册文档启动脚本。
- [renderer-bridge.mjs](https://github.com/keaipiao/codex-quota/blob/54e3e15c898b65d66db4ecffec1725c4d8e9275d/src/host/renderer-bridge.mjs)
  把归一化快照推到 renderer；[panel-inject.js](https://github.com/keaipiao/codex-quota/blob/54e3e15c898b65d66db4ecffec1725c4d8e9275d/src/renderer/panel-inject.js)
  用 DOM 结构定位侧栏、创建面板，并用 `MutationObserver` 处理路由与布局变化。
- [SECURITY.md](https://github.com/keaipiao/codex-quota/blob/54e3e15c898b65d66db4ecffec1725c4d8e9275d/SECURITY.md)
  承认 loopback CDP 不认证同用户进程，随机端口不能消除同用户附着风险；停止额度 daemon
  不会关闭宿主的 CDP，必须退出宿主再从官方入口启动。

该项目不修改 Store 包或 `app.asar`，但会改变运行时宿主 UI。它依赖 DOM 读取与注入，
与本仓库当前边界不一致；把定位点从侧栏换到标题栏仍无法消除这些依赖。

## 官方能力边界

[OpenAI plugin architecture](https://developers.openai.com/plugins/concepts/plugins)
描述 skills、MCP 和 hooks；[MCP UI quickstart](https://developers.openai.com/plugins/build/app-quickstart)
描述工具关联的 iframe UI。这次查阅的文档没有提供向桌面窗口标题栏注册常驻 widget 的 API。
因此不能把普通 MCP 卡片当作红圈位置的官方扩展点；这是检索范围内的结论，不是对所有内部能力的断言。

[Microsoft SetWinEventHook 文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook)
说明 OUTOFCONTEXT 不把回调映射到目标进程，注册线程需要消息循环；managed callback 需要
维护生命周期并处理重入。[DWM best practices](https://learn.microsoft.com/en-us/windows/win32/dwm/bestpractices-ovw)
要求顶层窗口进行点击穿透时组合 `WS_EX_TRANSPARENT` 与 `WS_EX_LAYERED`。
不能仅凭设置 `WS_EX_TRANSPARENT` 或返回 `HTTRANSPARENT` 就宣称跨进程点击穿透可靠。

## 调研阶段的建议接入方式

以下保留调研阶段建议；实际已实施的范围与差异见文末“本地实现”。

沿用[技术设计](../../TECH_DESIGN.md)中的 Runtime → Presentation → App 边界。

```text
现有 CLI / OAuth provider
    → QuotaRuntimeService.StateChanged
    → 标题栏紧凑投影（Core/Presentation）
    → 覆盖窗口控制器（App/Services）
    → 自有原生 HWND（App/Interop）
                  ↑
          宿主窗口识别 / 几何事件
```

1. 数据复用：在 [App.xaml.cs](../../../windows/src/CodexQuotaTray.App/App.xaml.cs) 现有
   `StateChanged` UI 分发路径增加覆盖窗更新。消费现有
   [AppUiState](../../../windows/src/CodexQuotaTray.Core/Models/AppUiState.cs) 与
   [QuotaWindowView](../../../windows/src/CodexQuotaTray.Core/Models/QuotaWindowView.cs)，
   保留可靠性、缺失值、stale、refreshing、错误状态与窗口名称。覆盖窗不启动第二个 provider、
   网络轮询或缓存，不重新解析 raw JSON。
2. 显示：优先显示两个可识别窗口的名称与剩余额度，例如合成示例“5h 54% · 周 13%”。
   名称按现有时长/标识识别结果生成，不把 primary/secondary 固定映射成五小时/七天；
   未知显示“—”，多窗口或空间不足按明确布局策略压缩，不能以零代替。
3. 定位：基于 HWND、进程/package identity、可见性与主窗口形态筛选，排除宠物、工具窗与
   popup。DWM bounds + DPI + 窗口按钮区域决定候选位置；菜单宽度与应用缩放需要原型验证，
   无法可靠识别空白区时保守隐藏或让用户调 offset，不采用固定截图像素坐标。
4. 跟随：用 OUTOFCONTEXT hooks 监听几何、前台、最小化与销毁，合并更新并复核目标身份；
   在消息循环线程注册、保持 delegate 存活、退出时 unhook。允许有界低频发现，避免持续
   全桌面高频扫描。几何事件不触发额度刷新。
5. 绘制：先验证自有 Win32 layered tool window；主客户端继续使用 WinUI。
   可复用 [NativeMethods](../../../windows/src/CodexQuotaTray.App/Interop/NativeMethods.cs)
   的现有互操作风格。WPF/WinForms 示例证明思路，但不代表可直接套到 WinUI 透明窗口；
   不为此引入新的 UI 框架或修改依赖版本。
6. 交互：第一版建议只读、点击穿透、不抢焦点、不单独出现在 taskbar/Alt+Tab；
   不设全局 topmost。先采用宿主在前台时显示，最小化、隐藏、销毁或识别失败时隐藏。
   如需后台仍可见，再单独验证 owner 与遮挡层级。
7. 账户：明确展示的是 CodexQuotaTray 当前 CLI/OAuth 来源的额度，不能声称自动匹配
   ChatGPT 登录账户，也不读取宿主认证或对话来比较身份。截图目标里的普通 ChatGPT 聊天
   消息上限与 Codex 套餐额度也不能混称。
8. 开关与身份：作为 Windows 可选展示能力，设置与生命周期沿用 Dev/Preview/Production
   隔离。Android 不参与。本轮不新增设置或改动正式身份。

## 原型需要解决的风险与验证

| 风险 | 后续验证方式 |
| --- | --- |
| 当前宿主识别规则与开源项目不同 | Dev/Preview 的假宿主离线验证；真实宿主窗口元数据与 GUI 验证需明确 opt-in |
| 菜单、按钮、标题栏高度、宿主缩放冲突 | 纯布局测试覆盖 DPI、窄窗口、负坐标、多显示器与文字长度；实际宿主验证需 opt-in |
| 跨进程点击穿透、标题栏拖动与双击最大化 | 原生假宿主集成验证；真实 GUI 验证需 opt-in |
| 最小化/销毁后残留、切换多窗口、HWND 重用 | 窗口状态机离线测试，检查所有 hook/native resource 的释放 |
| 覆盖窗与主面板数值或错误状态不一致 | 同一投影输入测试，包含来源切换、失败保留、未知窗口和 stale |
| CLI/OAuth 与宿主账户不一致 | UI 文案明确数据来源，不通过宿主私有认证建立自动绑定 |

普通 Windows 代码实现的最终验证为 [Windows README](../../../windows/README.md) 与
仓库 AGENTS.md 规定的 `Full`；如果实现改变 publish/deployment 产物，再按规则追加 `Release`。
调研阶段仅文档，验证为引用/本地 Markdown 链接检查与 `git diff --check`；后续本地实现使用 `Full`。

公开源码足以确认技术路线可行，但 Windows 10/11、当前 ChatGPT 版本、透明文字质量、
空白区边界与点击穿透仍需要后续原型证据。

## 本地实现

- 在“设置 → 外观”增加开关，旧设置缺失或 malformed 时默认关闭；保存到各自身份的数据目录。
- Runtime 推送的同一快照生成 full/compact 文本，按显示方式明确“剩余”或“已用”，最多显示两窗口并
  标记其他窗口数量；可靠性不足显示“—”，失败保留旧值并标记上次数据，来源切换沿用 Runtime 清空。
- App 管理自有 Win32 layered tool window，使用 GDI 字形 mask 与 premultiplied alpha 绘制普通灰色文字。
  `WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` 保持视觉透明与不激活，文字区域接收单击；
  自有覆盖窗通过 owner 关系保持在宿主上方，不使用全局 topmost。
- foreground 与目标 geometry/show/hide/minimize/destroy/cloak 事件触发合并定位；后台宿主持续显示，
  无宿主时只等待事件。启用与宿主销毁时有界发现已有窗口，位置事件不触发额度请求，无定时进程扫描。窄窗隐藏后仍保留 geometry
  监听，放大时重新显示。关闭开关或退出时释放 hooks、HWND、window class；每次测量/绘制释放 GDI handles。
- 原生初始化错误被隔离，主面板/托盘继续工作；诊断提供状态和错误码，开关关闭后重开可重试。

当前支持匹配 `OpenAI.Codex_2p2nqsd0c76g0` 的新版统一 ChatGPT/Codex Store 主窗口，
通过 package family、可执行文件名、固定主窗口标题、窗口类和 owner/tool-window 排除条件验证。
旧 ChatGPT Classic、非 Store 发行版与未来身份/标题变更未纳入支持。

几何使用 DWM frame、窗口按钮区域、显示器工作区和 DPI；顶部 inset 与左侧菜单预留是保守
常量，不读取宿主 DOM 或 UIA。全宽文本放不下时退化到单窗口文本，仍放不下时隐藏；
内部缩放、超宽菜单或新布局可能需要调整预留量。这些属于真实宿主验证的剩余风险，
本地构建与纯布局测试不能证明当前宿主上的视觉效果和跨进程鼠标行为。

2026-10-07 初始实现离线验证：仓库 `verify-winui.ps1 -Mode Full` 通过，Debug/Dev x64 构建
零警告、零错误，563 项离线测试全部通过。新增回归覆盖显示模式下的剩余语义、未知/非法值、
失败保留、来源清空、宿主筛选、DPI/负坐标/窄窗布局，以及设置迁移、保存、即时应用与写入失败。
全仓库本地 Markdown 链接和 tracked/untracked 文件空白检查通过。

### 前台时仍不显示：宿主遮挡覆盖窗

用户明确报告启用后的真实窗口故障后，使用只读 Win32 元数据探针复现。宿主身份、窗口类、
固定主窗口标题和 DPI 均匹配；覆盖窗已创建，位于正确标题栏区域。用户手动保持 ChatGPT
在前台时，宿主和覆盖窗都为 visible，但 EnumWindows 的 Z-order 中宿主位于覆盖窗上方。
因此问题不是应用启动顺序，也不是窗口筛选失败，而是未绑定 owner 的普通 popup 被宿主遮挡。

修复在显示前设置自有覆盖窗的 `GWLP_HWNDPARENT`，并确认 owner 确实是当前目标；只改变
覆盖窗，不修改宿主窗口。失去前台先隐藏、再解除 owner；`WM_NCDESTROY` 清空已销毁 HWND，
保留 hooks 与 class，下次识别到目标时重建覆盖窗。后续验证需确认 owner、Z-order、前台与
鼠标行为，而不能仅以 `IsWindowVisible` 推断用户实际看到了覆盖内容。

修复后的最终验证：`Full` 通过，Debug/Dev 构建零警告、零错误，564 项离线测试全部通过。
新增匿名、从不显示/激活的 Win32 HWND 测试，验证 owner 绑定、相对 Z-order、owner 销毁后
覆盖窗失效与新覆盖窗重新绑定；不操作真实宿主、账户或托盘。

用户手动保持 ChatGPT 在前台，重建并重新启动 Dev 后，只读实机检查确认
`HostForeground=true`、`OverlayVisible=true`、`OwnedByHost=true`，Z-order 为 overlay → host。
验证对应于本机当前宿主、200% DPI 和负坐标显示器；未读取宿主对话/认证、未重启宿主或关闭
Production。computer-use 仅用于启动和收起 Dev，ChatGPT 前台切换由用户完成。
真实鼠标穿透、拖动、最小化与宿主进程重启尚未逐项验证，不以窗口元数据代替这些交互证据。

### 标题栏视觉调整

用户要求去除胶囊和来源前缀，文字参考原生菜单。初始白色文字底色来自错误的
`SetBkMode(..., 2)`（OPAQUE），已改为 `1`（TRANSPARENT）；同时删除圆角背景和边框绘制。
采用普通字重的 Segoe UI、14 DIP，使用截图菜单“帮助”的主要灰色 `#8E8F90`，保留 24 DIP
行高、4 DIP 顶部 inset 与垂直居中，使文字中心位于标题栏菜单所在的 16 DIP 高度。
正常内容为“剩余 5 小时 64% · 7 天 92%”，不显示 CLI/OAuth 来源前缀；设置中的来源选择与
账户匹配边界保持不变。比例调整依然由宿主 DPI 决定，未读取宿主 DOM 或 UIA。

视觉调整后 `Full` 再次通过，564 项离线测试全部通过，构建零警告、零错误；本地 Markdown
链接和 tracked/untracked 空白检查通过。Dev 通过既有退出入口关闭、重建并重新启动；
菜单对齐与文字观感按用户截图匹配，最终视觉一致性仍需用户在宿主前台查看确认。

### 菜单基线与显示方式同步

后续截图中“帮助”的文字范围为 y=33–58，额度首段为 y=29–55，中心相差约 3.5 物理像素。
在已确认的 200% DPI 下，顶部 inset 从 4 DIP 调整为 6 DIP（向下 4 物理像素）。仍使用相同的
14 DIP 字体与垂直居中，文字样式不改变；其他 DPI 按同样的 DIP 位移缩放。

“个性化”中的应用主题移至第一组。“使用百分比”选项改名为“已用”，保留
`ShowRemainingPercent` 的既有设置合同。标题栏投影接受该设置，用可靠的规范化
`UsedPercent` 生成所选数值和“剩余/已用”标签，未知数据仍显示“—”。

设置保存事件直接更新标题栏的现有快照投影，不触发额外读取。这样即使额度恰好为 50%、
Runtime 因投影内容相同而未发布 `StateChanged`，标题栏仍立即更换标签；设置保存失败则
按实际持久化模式通知覆盖窗。阈值语义仍基于剩余额度，不随展示模式反转。

最终 `Full` 验证通过：Debug/Dev 构建零警告、零错误，568 项离线测试全部通过；新增回归
覆盖剩余/已用的数值和标签、未知窗口的两种显示模式、50% 标签切换与保存失败的模式通知。
XAML 定向检查确认个性化分组顺序为应用主题、额度显示、ChatGPT 标题栏；本地文档链接与
空白检查通过。Dev 已重新启动；新偏移在真实宿主上的最终视觉对齐仍需用户查看。

### 后台持续显示与性能验证

用户明确授权后台持续显示及与已运行正式版的性能对照。实现保留已识别宿主，不因其他应用
获得前台而隐藏；宿主 hidden/minimized/cloaked 时隐藏并等待恢复。启动时一次有界窗口发现
允许附着到已打开的后台宿主，新窗口出现通过 show/foreground 事件识别；销毁后有界重建。

背景显示时，将覆盖窗放在 owner 上方、其他覆盖应用下方，并使用 NOOWNERZORDER，避免
抬高宿主。单纯移动使用 NOZORDER；不设全局 topmost。文本测量仅缓存 full/compact 两项，
字体按 DPI 复用，关闭时释放；相同文本/DPI/绘图尺寸复用 raster，位置变化不主动重绘。

`Full` 通过，577 项离线测试全部通过，Debug/Dev 构建零警告、零错误。新增回归覆盖宿主
可见/最小化/cloak 组合、移动与内容/DPI/尺寸重绘判定，以及覆盖窗不会越过另一个窗口。
实机只读检查确认 Dev 启动后，在 ChatGPT 不处于前台时覆盖窗已 visible 且 owner 匹配。

性能采样只读比较已安装的 Production 0.11.9 与当前 Dev 0.11.9；正式版未关闭、修改或重启。
主进程 CPU 用累计 TotalProcessorTime 增量除以实际墙钟时间和逻辑处理器数；同时采样
Working Set、Private Bytes 与 GDI/USER handles。不计子进程、DWM/GPU 或网络性能，
不读取账户、会话正文、命令行或原始协议响应。另对同一 Dev 关闭/开启贴窗做配对采样，
以避免把 Debug/Release、运行时长、设置窗口和堆/缓存差异归因于贴窗。

#### 2026-10-07 实测结果

Windows 本机 22 个逻辑处理器，每轮每秒采样一次，共 30 次，实际墙钟约 30.9 秒。
设置开关操作后等待 10 秒，不把开关动画和保存操作计入样本。两者主面板收起；前三轮
Dev 个性化页保持打开，最后一轮将设置页也收起。Production 来自既有安装目录，
已运行约 175.6–175.8 小时；Dev 为 Debug，运行约 7–20 分钟。两者设置/缓存/构建类型
和历史资源状态不同，整程序横向差异不能被当作贴窗的因果增量。

| 场景 | 进程 | 平均 CPU（全机百分比） | 平均 Working Set（MiB） | 平均 Private Bytes（MiB） |
| --- | --- | --- | --- | --- |
| 开启，设置页打开 | Production | 0.01380% | 240.168 | 270.934 |
| 开启，设置页打开 | Dev | 0.32893% | 252.652 | 130.200 |
| 关闭，设置页打开 | Production | 0.00921% | 239.522 | 272.541 |
| 关闭，设置页打开 | Dev | 0.47646% | 252.268 | 129.915 |
| 再开启，设置页打开 | Production | 0.02302% | 235.086 | 265.814 |
| 再开启，设置页打开 | Dev | 0.26931% | 255.668 | 131.813 |
| 开启，所有面板收起 | Production | 0.01376% | 235.806 | 265.856 |
| 开启，所有面板收起 | Dev | 0.01834% | 257.761 | 134.455 |

对应本地采样结束时间（Asia/Shanghai）分别约为 20:44、20:47、20:51、20:57。
三轮开启样本均为 `HostForegroundSamples=0`、`OverlayVisibleSamples=30`、owner 匹配；
关闭样本为 `OverlayVisibleSamples=0`。已恢复开启并收起 Dev 设置页。

同一 Dev 的开启/关闭样本未呈现 CPU 正向增量；开启样本 Private Bytes 比关闭样本高
0.285–1.898 MiB，Working Set 高 0.384–3.400 MiB。这是短时差值范围，包含堆与界面缓存
波动，不能解释为固定贴窗内存，更不能据较低开启 CPU 声称它降低了 CPU。
面板全部收起后，Dev 与 Production CPU 相差约 0.00458 个百分点（约 1.0 ms CPU/秒）。
Dev 的 Working Set 高约 21.955 MiB，而 Private Bytes 低约 131.401 MiB，说明两种
内存指标和缓存历史都影响比较，不能把前者全部归因于此功能。

GUI 资源也存在历史差异：Production 本轮 GDI 计数约 7011–7023、USER 约 2390–2396；
Dev 约 GDI 136–157、USER 116–133。它们仅说明资源存量不同，未调查成因，未触碰正式版。
本轮结论限于静态后台驻留，未测量持续拖动/跨 DPI/后台刷新峰值、长期泄漏、子进程或 DWM/GPU。

### 点击额度文字手动刷新（后续交互增强）

在上述性能采样和初始实现提交之后，用户要求单击额度文字刷新，并显示“刷新中…”和最小
提示时间。覆盖窗的点击回调复用 `MainViewModel.RefreshCommand`，仍通过 Runtime 的 Manual
读取与同一状态提交路径，不创建新 provider 或缓存。标题栏显示本地请求等待状态与 Runtime
刷新状态；同主面板的最小提示时长内保持刷新提示，请求或提示未完成时忽略重复点击，
成功与失败均遵守最小时间，不因重复点击重置计时。

初始 color-key 绘图的空隙会穿透鼠标，删除 `WS_EX_TRANSPARENT` 仍不能让整段文字可靠可点。
因此改为白色 GDI 字形 mask → 灰色 premultiplied BGRA → `UpdateLayeredWindow`；空隙 alpha
为 1，文字按字形覆盖率设置 alpha，保留无可见背景/边框的样式并使整个文字矩形可点。
窗口继续使用 NOACTIVATE 与 MA_NOACTIVATE，鼠标为手型；按下/松开须在区域内且无明显
移动，拖动、移出或失去 capture 会取消刷新意图。区域之外的标题栏保持宿主原生交互。

绘制缓存和仅位置移动策略不变，新增的 alpha 转换仅发生在真正重绘时，无持续动画或输入
轮询。上面的性能数字对应点击增强前的实现，不能直接当作此次绘制改造后的实测值。
最终 `Full` 通过，构建零警告、零错误，581 项离线测试全部通过；新增回归覆盖透明空隙、
字形 premultiplication、拖动/移出取消、并发点击 gate 和无额度时的刷新标签优先级。
用户在 Dev 上试用后反馈“没问题了”，当前点击体验已获用户确认；未进行自动化真实账户
smoke 或精确的 GUI 时长测量，不把用户反馈扩展为未覆盖场景的自动验证结论。

重启 Dev 后只读检查确认覆盖窗 visible 且 owner 匹配；使用物理坐标的 `WindowFromPoint`
检查文字中心和透明边距，两者均命中覆盖窗，确认空隙并未继续穿透到宿主。该检查没有
发送鼠标消息或触发真实账户读取，不替代实际点击、最小时间和焦点行为的 GUI 验证。

### 倒计时重置截止时刻调度修复

修复提交：`1fe5181`。

在 `5624c50` 的固定 60 秒 Win32 Timer 中，如果重置发生在第一次触发之前，且没有其他窗口或数据
事件唤醒投影，“待更新”会延迟到后续触发。原 `DeadlineInsideTheSameMinuteUpdatesBeforeTheLastTimerStops`
只验证到期判断，不验证唤醒时间；本次通过代码确认此缺口，没有把它描述为真实账户现场复现。

修复维护同一个 HWND/Timer ID：下一次截止时刻取自然分钟边界与最近未来重置时间的较早者。
重复位置事件不会重设同一绝对截止时刻；触发后停止计时器并重新安排下一次，没有未来重置时停止。
投影和下一次截止计算使用同一时刻，绘制耗时跨越截止时则尽快补醒；毫秒延迟向上取整，遵循 Win32
最小 10 ms 间隔。该机制只更新界面，不改变额度读取、刷新或通知调度，不增加线程或网络轮询。

九项新增回归覆盖分钟边界、分钟内重置、多窗口最近截止、重复事件不推迟、过期/缺失时间停止、
UTC offset、跨天、日期边界及延迟舍入。原生回归调用与生产相同的 `SetCountdownTimer` 适配入口，在
匿名隐藏 HWND 上把同一 ID 的分钟计时器重设为 200 ms，实际读取提前到达的 `WM_TIMER` 消息。
这些匿名窗口不连接真实宿主或账户；它们验证 Win32 触发链，不能证明真实宿主 UI 线程在任意负载下
都无调度延迟。最终 `Full` 通过：617 项离线测试，构建零警告、零错误。

机制依据：[SetTimer](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-settimer) 的
相对间隔与同 ID 重设行为；[KillTimer](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-killtimer)
不会移除已排队消息，停止状态因此仍需在回调入口检查。
