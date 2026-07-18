# CodexQuotaTray Roadmap

状态日期：2026-07-18

当前状态：只读 MVP 与本地打包已完成；公开发布门禁尚未全部满足。

## 已完成

### P0：协议可行性

- 通过 stdio 启动并初始化 Codex App Server；
- 读取 `account/read` 与 `account/rateLimits/read`；
- 确认多 bucket、动态窗口和稀疏更新语义；
- 建立只读 method allowlist、匿名 fixture 与协议版本基线；
- 确认当前 schema 不提供权威 reset-credit count 或消费方法。

### P1：后台核心

- App Server process supervisor、JSON-RPC transport 和 typed protocol；
- 单一 normalized state reducer；
- 有界重启/backoff、请求超时与单 in-flight refresh；
- 失败保留最后有效额度、15 分钟 stale 和认证状态隔离；
- 隐私最小化的 settings/cache persistence；
- 完全离线的 fake-process 与状态转换测试。

真实进程 extended run 持续 21 小时 11 分，期间无 restart、warning 或遗留进程。该结果满足本地 MVP 证据，但不等同于完整 24 小时或七天发布质量测试。

### P2：原生 Windows 托盘

- 原生 Win32 托盘、动态卡片和标准右键菜单；
- Per-Monitor V2、嵌入式多尺寸图标与 DPI-aware 布局；
- fresh、refreshing、stale、offline、unauthenticated、unavailable 展示；
- 手动刷新、官方 Usage 页面、提醒、缓存和开机启动设置；
- Windows 11 x64 本地 smoke。

### P3：事件与提醒

- App Server 更新、卡片打开、系统恢复、网络恢复和可配置周期调度共用 refresh coordinator；
- 50%、20%、10% 阈值提醒与跨重启防重复；
- 同窗口/周期去重，尊重 Windows 安静时段；
- 系统事件 burst 不产生并行读取或通知风暴。

### P4：打包基线

- locked x64 release build；
- per-user ZIP 与 Inno Setup 安装器流程；
- 安装、升级、卸载、HKCU 开机启动和完整性校验；
- 隐私说明、依赖/许可证清单、unsigned developer-build 边界；
- Windows 11 上的自动化 package smoke。

## 当前硬性边界

- MVP 始终只读；
- 不抓取网页、不读取 Cookie 或 Codex token 文件；
- UI 不接触 raw JSON-RPC；
- 不固定 `primary`/`secondary` 的周期含义；
- reset-credit count 在协议明确提供前保持 unavailable；
- 不提交 `dist/`、`dist-inno/` 或 `target/` 构建产物。

## 公开发布门禁

以下事项尚未完成，因此当前产物只能标记为 unsigned developer build：

1. Windows 10 x64 安装、升级、卸载和托盘 smoke；
2. 稳定渠道 Windows 11 复验；
3. 100/125/150/200% DPI 与多显示器定位矩阵；
4. 组织控制的 Authenticode/Trusted Signing 和 provenance；
5. 单独安排的七天稳定性测试；
6. 自绘 quota rows 的辅助功能边界复审。

## Reset-credit 功能门禁

只读展示数量需要同时满足：

1. 新版 stable schema 明确提供权威 available count；
2. schema diff 与匿名 live observation 一致；
3. parser、missing/null/malformed fixture 与回归测试齐全；
4. 产品重新确认展示范围。

消费重置额度不属于上述能力，必须另行完成确认 UX、幂等、审计、失败恢复和威胁建模。

## 推荐下一任务

优先完成 Windows 10/DPI/多显示器验证；准备公开发布时再接入组织控制的签名与七天 soak。不要自动发布未签名产物，也不要自动恢复已结束的旧 soak。

## P5：0.2.0 小而美升级（已实现）

- 紧凑标题、动态套餐 Badge、明确状态语义、单行重置信息与自适应 DPI 布局；
- 50%/20%/10% 持久化提醒、非追溯基线、本地伪匿名窗口键和 at-most-once 保存顺序；
- Auto、5/15/30 分钟与 ManualOnly 模式、统一刷新协调、动态 stale 与失败退避；
- 旧设置迁移以及 ZIP/Inno 默认删除、显式保留用户数据的一致语义。
