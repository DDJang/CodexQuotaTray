# Windows LAN 间歇性 TCP Connect Timeout：调查方案

- Area: Windows / Android LAN sync
- Baseline: `main@7cd4fe8ad834248b21a6c542b82753d577b820e7`, app `0.11.5`
- Status: Active
- Date: 2026-09-06

## Phase 0 实施进度（2026-09-12）

已同步 `main@b4b2c20`，本轮仅增加观测与诊断文案，不调整 timeout、retry、配对协议或网络恢复行为。

- Windows 沿用 `LanDiagnosticBuffer` 的有界轮转与脱敏通道。事件保留 UTC、随机进程
  `processSession` 与 `monotonicMs`；单调值是系统单调时钟毫秒值，不是进程 uptime，也不能
  与另一台设备直接比较。跨进程、跨设备先用 UTC 对齐，同一 session 内用单调差值确认顺序/耗时。
- 每次 bind 尝试分配递增 `listenerGeneration`，记录 starting、started、bind-failed、stopping、
  socket-stopped、stopped；controller 记录 restart-requested 及原因。原来提前报告 stopped 的
  事件改为 stop-requested，停止完成在 dispose 后记录。异常只写类型与 socket 错误码，不写原始消息。
- accept 分配 listener 内的 `connectionId`，与 request/closed 事件共同记录远端地址、远端端口、
  本地 endpoint。日志首次见到连接的边界是 `AcceptTcpClientAsync` 返回；队列和 TCP 握手仍需抓包。
- 网卡候选日志增加 prefix、状态与索引；start/reconcile 和诊断导出复用本地枚举。候选字段不会
  覆盖已记录的绑定接口。network profile、SSID/BSSID、网卡电源状态仍未采集，显示 unavailable；
  listener 摘要仍是应用观测，不是 OS LISTEN/端口所属进程快照。
- Android 拆分 Last recorded attempt、Last success、Last failure。历史成功/失败的 attempt ID
  未单独持久化，明确显示 unavailable，不把最新 attempt ID 归给历史失败；细节仍需完整事件日志。
  Quota 元数据保存日志明确标记 `endpointChanged`，Token relocation 同时比较 host 与 port。

本轮离线验证覆盖连接关联、listener generation、日志故障隔离、时间线持久化、导出采样故障、
Android 摘要归属与 endpoint 日志。尚未获取真实失败的双端抓包证据，调查维持 Active。
下一步为 Phase 1：在 Dev/Debug 现场保存失败前后完整日志、TCP 包头与 OS listener 快照，
再按 Phase 2 归因；本轮没有启动抓包或改变正式版运行状态。

## 背景与现象

Android 端 LAN Diagnostics 已捕获到一次具有较高诊断价值的间歇性连接失败。失败发生在 TCP 建连阶段，而不是 pairing、HTTP、quota/token 协议或 JSON 处理阶段。

2026-09-06 18:43（UTC+8）的连续样本：

| Attempt | Channel | 结果 | 关键耗时/现象 |
| --- | --- | --- | --- |
| 35 | quota | `TCP_CONNECT_TIMEOUT` | `connect()` 2,000 ms 超时，未进入 `TCP_CONNECTED` |
| 36 | quota | `SUCCESS` | 同一 endpoint，HTTP 200，64 ms |
| 37 | token | `SUCCESS` | 同一 endpoint，HTTP 200，58 ms |
| 38 | quota | `SUCCESS` | 同一 endpoint，HTTP 200，43 ms |

失败与后续成功期间，下列 Android 网络上下文保持一致：

```text
endpoint=172.26.32.198:43821
interface=wlan0
local=172.26.117.177/17
route=172.26.0.0/17
networkHandle=656240726029
networkGeneration=9
socketBoundToNetwork=true
generationChangedDuringConnect=false
```

`172.26.32.198` 位于 Android 的直连 `/17` 路由中；失败后约 12 秒，相同 network、route、endpoint 和 generation 即恢复成功。因此当前证据明显降低了以下方向的优先级：

- Android 选择了错误的 `Network`；
- socket 未绑定 Wi-Fi；
- connect 期间发生 network generation race；
- endpoint 已失效或地址迁移；
- pairing/token/quota HTTP 语义错误；
- 服务端返回 HTTP 错误后被误分类为 TCP timeout。

当前缺口是 Windows 侧与链路层缺少同一时间线上的证据。Android 公共 API 也无法可靠读取邻居/ARP 状态，当前 diagnostics 已明确记录 `neighborCollection=unsupported_by_public_android_api`。

## 调查目标

这轮调查只回答一个问题：

> 当 Android 对已知可用的 Windows LAN endpoint 发起连接却得到 `TCP_CONNECT_TIMEOUT` 时，TCP SYN 实际停在了哪里，以及当时 Windows listener 是否处于可接受连接的状态？

最终需要把真实失败归入至少一个可证实的类别：

1. Windows LAN listener 生命周期 / bind / accept-loop 问题；
2. Windows Firewall / WFP / 主机网络栈丢弃；
3. Wi-Fi AP、ARP/邻居解析或二层客户端转发问题；
4. Windows 网卡休眠、重连、网络配置切换造成的短时不可达；
5. Android 到 AP 的链路侧异常；
6. 其他由抓包和双端时间线直接证明的原因。

## 非目标

在拿到根因证据前，本轮不做以下行为改动：

- 不单纯把 quota `connectTimeoutMs=2000` 调大；
- 不增加无依据的额外重试、延时或指数退避；
- 不重新设计 pairing、token、quota API；
- 不重写 Android network binding / route matching；
- 不因为偶发 timeout 就自动重新配对；
- 不使用“后续请求能成功”作为根因已解决的判据。

这些手段可能改善表面成功率，但会掩盖真正的瞬时不可达窗口。

## 当前假设与优先级

### H1：Windows listener 存在短暂的 stop/rebind/accept 空窗

优先级：高。

需要确认失败时刻：

- 43821 是否仍处于 LISTEN；
- listener 是否刚发生 start / stop / restart / rebind；
- listener generation 是否变化；
- TCP 已完成握手但应用 accept-loop 没有及时接收；
- 是否有异常触发 listener 恢复路径。

如果 Windows 对失败连接返回 RST，则这一方向优先级进一步升高。

### H2：Windows Firewall / WFP / 主机网络栈短暂丢弃入站 SYN

优先级：高。

如果 Windows 抓包能看到 Android SYN 到达，但看不到对应 SYN-ACK 或 RST 离开，且 listener 同时确认仍在监听，应继续调查 Windows Defender Firewall、WFP filter、网络 profile 切换和主机网络栈状态。

### H3：Wi-Fi / ARP / 二层客户端转发瞬时异常

优先级：高。

如果 Android 开始 connect，但 Windows 抓包完全看不到 SYN，则问题发生在 Windows 主机之前。重点包括：

- Android 对 Windows IPv4 的 ARP/邻居解析；
- AP 对同一 WLAN 内客户端间帧转发；
- 5 GHz Wi-Fi 的瞬时重试/漫游；
- AP isolation、band steering 或短时客户端状态变化。

Android 公共 API 无法直接提供完整邻居表，因此应主要依赖 Windows 抓包以及必要时的 AP/路由器侧证据。

### H4：Windows 网卡电源状态、重连或网络 profile 切换造成短时不可达

优先级：中高。

需要把以下事件与 timeout 对齐：

- Windows sleep / resume；
- WLAN disconnect / reconnect；
- adapter `OperationalStatus` 变化；
- IPv4 地址 / prefix / interface index 变化；
- NetworkCategory / profile 变化；
- 应用收到的 Windows network-change 事件。

### H5：Android 侧的二层发送/返回路径异常

优先级：中。

当前 Android `Network` 选择、route matching、socket binding 与 generation 已有较强正向证据，因此不优先继续修改这一层。但若 Windows 抓包看到完整 SYN -> SYN-ACK，而 Android 未回 ACK，或 Windows 的 SYN-ACK 持续重传，则需要重新调查 Android/AP 返回路径。

### H6：2 秒 connect timeout 对偶发 LAN 抖动过于激进

优先级：低，作为韧性策略而不是根因。

只有在抓包证明 TCP 握手只是稳定地晚于 2 秒完成、而不是 SYN/SYN-ACK 丢失或 listener 空窗时，才评估 timeout 值是否合理。

## Phase 0：只补观测，不改变连接行为

### 0.1 Windows listener 生命周期日志

为 Windows LAN listener 增加低成本、可持久化的结构化诊断事件。至少记录：

```text
listenerGeneration
listenerStarted
listenerStopping
listenerStopped
listenerRestartRequested
listenerRestarted
bindAddress
bindPort
processUptimeMs
reason
exceptionClass
exceptionMessage
```

要求：

- 每次创建新 listener 都分配单调递增 `listenerGeneration`；
- start / stop / restart / bind failure 必须可在时间线上区分；
- 记录 wall-clock UTC 时间，同时优先增加进程内 monotonic elapsed 值，避免系统时间调整造成顺序歧义；
- 日志不得包含 pairing secret、token 或完整敏感请求内容。

### 0.2 Windows accept 级日志

每个已接受连接至少记录：

```text
listenerGeneration
acceptedAt
localEndpoint
remoteAddress
remotePort
requestChannel/path（若在 accept 后才能判定，可后补）
connectionResult
```

目标不是长期做高量网络 tracing，而是回答：Android timeout 对应的连接是否曾进入 Windows accept 路径。

如果现有实现抽象层拿不到原始 `Accept` 时刻，也至少记录“服务端首次观察到该 TCP connection / request”的最早时刻，并在文档中注明观测边界。

### 0.3 Windows 网络上下文快照

在 listener start/restart、网络变化事件以及诊断导出时记录：

```text
adapter identity/name
interface index
OperationalStatus
IPv4 address/prefix
bind address/port
network profile/category（可可靠获取时）
lastNetworkChange
lastNetworkChangeReason
```

如果能可靠、低成本获取，再补：

- WLAN SSID/BSSID/frequency；
- system sleep/resume 最近时间；
- 网卡电源/连接状态。

不能可靠获取的字段应明确为 unavailable，不要用猜测值填充。

### 0.4 诊断导出格式修正（仅语义，不作为根因修复）

当前 Android 顶部摘要容易把“最新 attempt”和“上一次 failure phase”并排展示，从而让人误读 `attempt=38` 是失败 attempt。后续可拆成：

```text
Last attempt:
  attempt=38
  channel=quota
  result=SUCCESS

Last success:
  attempt=38
  timestamp=...

Last failure:
  attempt=35
  timestamp=...
  phase=TCP_CONNECT_TIMEOUT
```

同时检查：

```text
Quota LAN relocated endpoint persisted=true
```

是否在 endpoint 实际未变化时也会打印。如果只是“成功后持久化 endpoint”的通用路径，应改成更精确的日志文案；真正发生 relocation 时再使用 `relocated`。

这两项是 diagnostics 可读性修正，不应与 TCP 根因修复混在一起。

## Phase 1：捕获一次真实 timeout 的双端证据

### 1.1 Android 必留证据

发生失败后立即保存完整 LAN Diagnostics，至少保留：

- attempt id；
- channel；
- `connectStart` / `connectFailed` 时间；
- endpoint；
- local IPv4/prefix；
- route prefix；
- networkHandle；
- attempt/binding/connect generation；
- `generationChangedDuringConnect`；
- socket binding 状态；
- 前后至少 2 次连接 attempt 的结果与耗时。

不要只截取失败行。失败前后的成功 attempt 是证明 network context 是否发生变化的重要对照。

### 1.2 Windows 应用诊断

保存同一时段前后至少 30 秒的：

- listener lifecycle；
- accept/connection events；
- network-change events；
- 应用异常与恢复动作；
- 当前 bind/listen 快照。

所有时间统一保留 UTC，UI 可以额外显示本地时间。

### 1.3 Windows 环形抓包

在可复现场景中开启 Windows 侧低开销环形 packet capture，只关注与 LAN server 端口相关的 TCP 流量；可以使用 Windows 内置抓包能力或 Wireshark/Npcap。调查阶段不要依赖人工在 timeout 发生后才开始抓包，因为瞬时失败已经过去。

要求：

- 至少能判断 Android -> Windows 的 SYN 是否到达；
- 能判断 Windows 是否发送 SYN-ACK 或 RST；
- 能看到 SYN/SYN-ACK 重传；
- 保存 timeout 前后约 10～30 秒窗口即可，避免长期保存无关流量；
- 记录抓包主机、接口、时区和过滤条件；
- 证据包不得上传 pairing secret、Authorization 内容或无关隐私流量。

如果抓包工具无法在不采集 payload 的情况下满足目标，优先使用只保留必要 header/元数据的配置。

### 1.4 失败现场的 listener 快照

如果能在诊断工具中自动完成且成本可控，在 Windows 端记录 43821 的 listen 状态快照，例如：

```text
port=43821
isListening=true/false
owningProcess=<expected/unexpected>
boundAddress=<address>
```

不要让额外 shell/PowerShell 调用进入高频正常请求路径；可以在 listener 状态变化、诊断导出或明确的故障采样路径触发。

## Phase 2：按包级证据归因

拿到一个真实 timeout 后，严格按下面的判定表分类，不凭“后面恢复了”推断。

| Android 现象 | Windows 抓包 | Windows listener/accept | 优先结论 |
| --- | --- | --- | --- |
| `connect()` timeout | 完全看不到 SYN | listener 是否正常均不能直接定因 | Windows 主机之前：Android/AP/ARP/L2 路径 |
| `connect()` timeout | SYN 到达，无 SYN-ACK/RST 离开 | listener=LISTEN | Windows Firewall/WFP/主机网络栈 |
| `connect()` timeout | SYN 到达，Windows 发 RST | listener 不在 LISTEN / generation 正切换 | listener lifecycle/bind 空窗 |
| `connect()` timeout | SYN 到达，Windows 发 RST | listener 明确在 LISTEN | 检查绑定地址、端口归属、抓包匹配与主机栈 |
| `connect()` timeout | SYN -> SYN-ACK，SYN-ACK 重传，Android 无 ACK | listener 正常 | 返回路径/AP/Android 侧 |
| `connect()` timeout | 三次握手在 2 秒内完整完成 | 应用无 accept/最早 request 事件 | Windows listener/accept-loop/框架层 |
| `connect()` timeout | 三次握手完整，且 Windows 有对应 accept | 两端记录矛盾 | 先核对五元组、时间戳和 attempt 对应关系，再调查 socket 层 |
| 成功 | 正常三次握手 | 正常 accept | 作为失败样本的同环境基线 |

如果证据不能落入某一行，先补观测，不进入修复阶段。

## Phase 3：最小化复现矩阵

优先使用真实应用请求节奏，不为了“更容易失败”先引入超高频探测。至少覆盖：

| 场景 | 目的 |
| --- | --- |
| Windows/Android 均保持活跃、稳定 Wi-Fi | 建立正常基线 |
| Windows 刚启动 LAN listener | 排查初始化窗口 |
| Windows listener 发生已知 restart/recovery | 排查 stop/rebind 空窗 |
| Windows sleep -> resume 后 | 排查网卡/网络栈恢复窗口 |
| Windows Wi-Fi 短暂断开 -> 重连 | 排查 network-change recovery |
| Android 锁屏/解锁后 | 排查 Android Wi-Fi 省电/恢复，仅作为对照 |
| quota 与 token 相邻请求 | 检查是否只有某 channel 触发，或其实是共用 TCP 可达性问题 |
| Windows Wi-Fi vs Ethernet（条件允许） | 区分 Windows WLAN/AP 路径与 listener 本身 |
| 2.4 GHz vs 5 GHz（条件允许） | 验证 AP/band steering/无线链路相关性 |
| Windows Public vs Private profile（仅安全可控环境） | 验证 firewall/profile 相关性 |

每个场景都应同时保留 Android diagnostics、Windows listener diagnostics 和必要的 packet trace。没有双端时间线的单次“成功/失败”不作为定因证据。

## Phase 4：按根因选择修复，而不是统一加重试

只有 Phase 2 已完成归因后才实施对应修复，例如：

- listener lifecycle：消除 stop/rebind 空窗，或保证 replacement listener ready 后再切换；
- accept-loop：修复异常退出、取消 token、并发或 dispose race；
- firewall/profile：修复规则 scope/profile/binary identity，避免运行时反复改 firewall 作为 workaround；
- network change：调整 Windows listener 的恢复时机和幂等性；
- AP/L2：如果属于环境不可控丢包，再单独评估客户端容错、timeout/retry 策略；
- Android 返回路径：基于包级证据再调整 Android network handling。

任何 timeout/retry 调整都必须作为“韧性改进”单独说明，不能替代根因结论。

## 验证计划

修复后至少执行：

1. 原始触发场景回归，确认失败窗口消失或行为符合预期；
2. 稳定 LAN 下持续 quota/token 请求，确认没有新增 listener restart、连接泄漏或错误恢复；
3. Windows sleep/resume、Wi-Fi reconnect 和 listener recovery 回归；
4. Android network generation 变化时继续确认 socket 绑定到正确 Wi-Fi network；
5. pairing、quota、token 正常路径全部保持兼容；
6. diagnostics 导出不得包含 token、pairing secret 或敏感 payload；
7. 若最终调整 timeout/retry，分别验证正常 LAN 延迟、故障恢复时间和电量/请求放大影响。

不规定一个脱离真实请求节奏的“必须 N 次零失败”数字来代替根因验证。验收重点是：已捕获的失败类型有明确机制解释，修复前能通过证据触发/观察，修复后同类机制不再出现。

## 调查完成标准

只有同时满足以下条件，才能把本调查从 `Active` 改为 `Resolved` 或 `Closed`：

- 至少捕获 1 次真实 `TCP_CONNECT_TIMEOUT` 的 Android + Windows + packet-level 对齐证据，或者通过受控复现得到等价证据；
- 能明确回答 SYN 是否到达 Windows、Windows 是否回 SYN-ACK/RST、listener 当时是否在监听；
- 根因结论由上述证据支持，而不是仅根据后续自动恢复推断；
- 已完成对应最小修复，或有证据证明属于不可由应用修复的环境问题；
- 修复后通过相应稳定 LAN、sleep/resume、network reconnect 回归；
- 如果结论改变当前 LAN 架构、恢复策略、诊断契约或安全规则，同步更新对应权威 `docs/` 文档；
- 在 `docs/investigations/README.md` 中填写 Outcome、Resolved by 和 Last verified。

## 下一次 timeout 的最小证据包

下一次现场只需要优先保住以下四样，避免先做大量无关排查：

1. Android 完整 LAN Diagnostics（含失败前后 attempts）；
2. Windows 同一时间窗口的 listener/network diagnostics；
3. Windows 端口 43821 的 packet capture 片段；
4. 当时的 Windows listen 状态与 sleep/network-change 时间线。

这四项足够把大部分候选根因直接压缩到 listener、Windows host、AP/L2 或 Android return path 中的一类。
