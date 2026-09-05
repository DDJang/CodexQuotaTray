# Android 液态玻璃长按高光调查与方案

调查日期：2026-09-05。代码基线：`974ec4631e1c45f426538d11e66e7d7e6e70ec7a`。
状态：方案 A 已否决，方案 B 已实现为候选版本；尚未验证真机观感，本文件不改变现行产品合同。
范围：主页面 `LiquidBottomTabs`，尤其是从未选中 Tab 开始的按住与拖动。

## 结论与建议

缺失高光首先是输入路径问题：调查基线中的 `InteractiveHighlight` 只监听移动胶囊上的手势，
未选中 Tab 的 press preview 和 drag handoff 没有驱动它。两种候选都复用现有 shader，
无需换玻璃库、改渲染拓扑或增加另一层白色遮罩。

但修复事件路径不能提前知道用户是否要长按。在普通触摸事件中，静止长按与稍后松手的 tap
有相同的事件前缀。因此，“静止长按从按下即有高光”“所有原有 tap 视觉完全不变”
“不等待时间或其他可区分信号”无法同时成立。这是本次分析的可行性判断，不能用调快 spring 消除。

本轮先实现方案 A 后进行用户验收，结论是方案 A 不可接受；现已改试方案 B：

1. **方案 A（已否决）**：未选中 Tab 越过现有拖动门槛、成功接管后才接入高光。
   用户验收确认：长按不松手时，必须先任意滑动一段距离才有高光；一旦停止滑动就没有高光。
   这不满足“静止长按也应保持高光”的目标，故不再作为实现方案。
2. **方案 B（本轮实现）**：只要当前按压拥有未选中 Tab 的 preview，就让高光随胶囊实际位置
   接近目标连续增加，与已有形变同步；不等待系统长按、不新增离散 reveal。它接受较慢 tap
   可能出现少量高光的取舍。

如果上述取舍不可接受，应保留静止长按现状。当前没有证据支持一个同时满足全部限制的第四条路线。

## 当前实现与根因

以下结论来自调查基线与现有回归约束；没有将静态阅读描述成真机复现。

| 入口 / 文件 | 当前行为 | 对本问题的意义 |
| --- | --- | --- |
| [LiquidBottomTabs.kt](../android/app/src/main/java/com/codexquotatray/android/liquidglass/LiquidBottomTabs.kt) | 可见 Row 和隐藏捕获 Row 都绘制 `interactiveHighlight.modifier`；preview 使用胶囊实际动画位置计算接触强度，drag handoff 从当前 preview 强度过渡到按压强度 | preview/hold 和 handoff 共用一次局部高光绘制 |
| [DragGestureInspector.kt](../android/app/src/main/java/com/codexquotatray/android/liquidglass/DragGestureInspector.kt) | `inspectDragGestures` 在 `awaitFirstDown` 后立即调用 `onDragStart`，没有等 touch slop 或 long-press timeout | 选中胶囊上的按住本来就能启动高光；“拖动高光”这个叫法掩盖了它实际由 down 驱动 |
| [InteractiveHighlight.kt](../android/app/src/main/java/com/codexquotatray/android/liquidglass/InteractiveHighlight.kt) | 直接胶囊手势仍用独立 spring；方案 B 通过可复用绘制接收 preview/handoff 强度，handoff 结束时从当前值单一回落 | 两条路径只绘制一次，不新增 prepare/reveal 生命周期 |
| [LiquidBottomTab.kt](../android/app/src/main/java/com/codexquotatray/android/liquidglass/LiquidBottomTab.kt) | `clickable` 发出 Press/Release/Cancel；`detectDragGestures` 负责未选中 Tab 的拖动接管 | handoff 仍由既有 press identity 和 drag ownership 驱动 |
| `LiquidBottomTabs` 的 `onPress` | 立即 `press()`，未选中时设置 `previewIndex`，调用 `settleToValue` 移动胶囊 | 已有按下反馈和预览；静止按住不提前提交页面 |
| `LiquidBottomTabs` 的 `onDragStart` / `onDrag` | 接管时检查 press/index/preview 所有权，首次增量从当前动画值起算，后续从 target 起算；handoff 从当前 preview 强度连续过渡 | 保证预览转拖动不跳回；不因模式切换突亮 |
| [DampedDragAnimation.kt](../android/app/src/main/java/com/codexquotatray/android/liquidglass/DampedDragAnimation.kt) | `pressProgress` 驱动折射、边缘高光、阴影；`settleToValue` 不注入拖动速度 | 普通按住已有边缘亮度和形变，但不是 InteractiveHighlight 的局部加色光斑 |

这里必须区分两种“高光”：`Highlight.Default.copy(alpha = progress)` 是 backdrop 的边缘高光；
`InteractiveHighlight` 是 Row 上的加色底光与局部径向光斑。只增强前者不能算完成本任务。
当前局部高光中心由胶囊的动画位置计算，`position` 回调忽略传入的手指 offset；补齐时应沿用
这一坐标方式，以及当前 shader 与不支持 RuntimeShader 时的 fallback。

Compose 对同一 pointer 使用首次命中的事件链，胶囊后来移到手指下方不会自动取得该 pointer。
因此不能依靠预览动画“把手指移交给胶囊”，也不能只在长按时重挂 `gestureModifier` 期待重放 down。
这是由当前组件结构结合 [Compose 命中与事件分发规则](https://developer.android.com/develop/ui/compose/touch-input/pointer-input/understand-gestures#event-dispatching-and-hit-testing)
得出的判断。

## 已失败路线与不采用的替代

前三项的观感与失败结果来自本次用户提供的实验记录；本轮未重新执行。
检索可达 Git 历史中的 `prepareAt` 未找到对应实现，不能据此补写不存在的实验 commit 或性能数据。

| 路线 | 问题 | 本方案约束 |
| --- | --- | --- |
| 未选中 Tab 长按确认后启用 InteractiveHighlight | 先等 `longPressTimeout`，视觉天然晚一拍 | 不再作为推荐路线 |
| 长按确认后启动高光 spring | 识别等待之后再经历 `pressProgress 0→1`，形成两段延迟 | 不通过更高 stiffness 包装成新方案 |
| down 时预热、长按成立后 reveal | 识别等待仍在，显示阶段仍割裂；`prepared/visible/job/timeout` 及清理竞态增加复杂度 | 不恢复 prepare/reveal 生命周期 |
| 所有 tap 在 down 时立即发光 | 改变满意的单击视觉，短促闪光 | 不采用 |
| 缩短或自定义 long-press timeout | 仍依赖时间，同时把部分慢 tap 重新归类 | 不作为无损优化；不覆盖系统长按配置 |
| 长按确认时 `snapTo(1f)` | 只能消除第二段动画等待，不能消除识别等待，还可能突亮 | 不当作自然、无延迟的解法 |
| 方案 A：只在 drag handoff 后显示高光 | 用户验收为“必须滑动才有，停住就没有”，静止长按没有持续反馈 | 已否决，改试方案 B |
| 依靠指压、接触面积或微小抖动预测意图 | 不能可靠区分所有 tap/hold，跨设备行为不稳定 | 不引入输入启发式 |

官方 `awaitLongPressOrCancellation` 的
[参考实现](https://android.googlesource.com/platform/frameworks/support/+/eeade07c1fa47231e3c7a30afdebd5d0d91f1832/compose/foundation/foundation/src/commonMain/kotlin/androidx/compose/foundation/gestures/DragGestureDetector.kt)
也使用 `viewConfiguration.longPressTimeoutMillis`。引用用于解释识别机制；它不是本项目依赖的
精确版本源码，依赖基线仍以 [app/build.gradle.kts](../android/app/build.gradle.kts) 为准。

## 方案 A：先补齐未选中 Tab 的拖动接管（已否决）

这是不要求改变静止 tap 的局部方案，已完成实现并被用户验收否决：

> 长按时不松手，必须任意滑动一段距离才有高光；不再滑动就没有高光。

因此它只能修复 drag handoff 的路径缺口，不能满足静止长按的反馈目标。下面的约束保留为
失败方案记录，不再作为当前实现验收标准。

1. 保持现有 `clickable`、press preview、拖动门槛与提交规则。只有现有 `onDragStart(index)`
   返回成功后，才允许该手势驱动接管高光。不能仅用 `dragInProgress` 判断物理拖动，
   因为选中胶囊路径在 down 时就把它设为 true。
2. 从 `InteractiveHighlight` 抽出内部可复用的**绘制部分**，其输入为高光强度与中心位置。
   原对象继续保留原手势和 spring 默认行为；底栏对接管路径增加局部强度来源。
   不新增 `prepareAt/reveal` 外部控制接口，不修改 `LiquidSegmentedTabs` 的行为。
3. 接管期间以当前 `dampedDragAnimation.pressProgress` 的限幅值作为高光强度，
   复用已经服务于按压形变的进度，不再启动一个高光 `0→1` spring。
   在长按后拖动场景它通常已升高，但不能假定固定等于 1；快速拖动使用实际当前值。
4. 两条路径最终只绘制一次高光：直接胶囊手势沿用原高光强度，接管手势使用接管强度。
   不能额外叠加两个白色光层，不能改变可见 Row → 隐藏捕获 Row → combined backdrop 的结构。
5. End/Cancel 后接管强度应从当前值回落，不能因清除 `handoffDragIndex` 直接灭灯。
   至多需要一个底栏局部强度动画及其单一驱动生命周期；它只在真实接管后使用，不在 down 时预热。
   新接管必须中止旧回落；取消、离开组合、外部切页都要终止旧手势的写入。

“复用进度”仅限这条已经成为拖动的路径。若直接对所有 preview 绘制
`dampedDragAnimation.pressProgress`，普通 tap 也会发光，等同于明确不采用的路线。
接管时从零接入较高亮度是否突兀，仍是视觉风险；取消第二段 spring 并不保证自然。

事件所有权应沿用现有 press identity 与 handoff 标记。`clickable` 因拖动消费而发出的 Cancel
不能关闭已经接管的光效或把胶囊拉回。真正的 drag cancel 才回到 committed index；drag end
提交最近 Tab 且至多一次。不要为了高光额外加入会消费 down/up 的长按 detector。

## 方案 B：静止长按候选——随预览贴合连续发光（本轮实现）

本轮接受“较慢的 tap 可能有少量高光”的取舍并实现这一路线。目标是让亮度成为已有预览
动作的一部分，而不是预览结束后另起一次提示；它仍是候选版本，需真机观感确认。

候选只使用当前有效按压、`previewIndex`、胶囊实际位置和已有形变进度：

```text
distance = abs(pillAnimatedValue - previewIndex)  // 单位为 Tab 宽度
contact = 1 - smoothstep(nearDistance, farDistance, distance)
```

上式表达“越接近越亮”。当前实现取 `nearDistance = 0.08`、`farDistance = 0.45`，候选强度为
`clamp(pressProgress, 0, 1) * contact`，只在同一有效 press 拥有 preview 时生效。
这两个距离是 Debug 候选参数，后续可根据真机对比调整；未引入额外毫秒阈值。

它不等待系统 long press，不在隐藏状态运行另一条高光 spring，也没有离散 reveal。
只用实际位置，不能用 `targetValue`（按下即跳到预览目标，会立即亮）。
拖动接管时从当前 preview 亮度连续过渡到当前 `pressProgress`，释放时从当前亮度回落；
不能在模式切换处跳亮。

这只是**视觉反馈策略**，不是长按识别器。必须正视以下限制：

- 胶囊接近目标依然需要动画时间；消除的是“等长按、再出光”的阶段感，无法承诺从 down 零延迟。
- 较慢的 tap 只要持续到贴合区域就会亮；若仍禁止这种变化，应直接否决候选。
- 快速反向点击、预览已接近目标、动画减弱/关闭时，贴合可能很早成立，普通 tap 更容易闪光。
- 抬手后预览动画仍可能继续；必须以有效按压约束新高光，不能在松手后的 settle 中补亮。
- 到达事件再 `reveal`、等 `pressProgress` 越过阈值再开 spring，都会退回失败路线；不做这些变体。

## 后续实现范围与验证方案

本轮改动涉及 `liquidglass/InteractiveHighlight.kt` 的内部绘制复用、`LiquidBottomTabs.kt`
的 preview 接触函数与 handoff 连接、相关回归测试，以及 Debug fixture 的验收说明。
`LiquidBottomTab.kt` 只有在现有回调确实不足时才调整；不重写整个手势系统。
保留 [PRD](PRD.md) 的底栏页面切换边界和 [TECH_DESIGN](TECH_DESIGN.md) 的 Android 架构。

现有 [SettingsStructureTest.kt](../android/app/src/test/java/com/codexquotatray/android/SettingsStructureTest.kt)
中的 `interactiveHighlightsKeepTheOriginalDirectGestureModel`、
`liquidBottomTabsKeepTheProductionCombinedBackdropRenderGraph` 和 fixture 检查，明确保护无
prepare/reveal/long-press job 的现状；本轮另补充 preview 距离函数的离线回归测试。指针命中
链、事件消费和视觉时序仍需 Compose 输入测试或 fixture 实测，不能简单删掉断言来迁就实现。
[MainTabStateTest.kt](../android/app/src/test/java/com/codexquotatray/android/MainTabStateTest.kt)
只验证页面状态，不证明指针路由或高光时序正确。

纯逻辑测试应验证接管所有权、结束/取消、旧回落被新手势打断以及提交次数。
指针命中链、事件消费和视觉时序需要 Compose 输入测试或 fixture 实测；当前构建未配置
Compose instrumentation 测试依赖，不能承诺现有 JVM 测试已覆盖这些能力。
若需新增测试基础设施，应另行明确范围，不能在本次文档调查中更改依赖。

后续进行 GUI 对比时，使用已有
[LiquidBottomTabsFixtureActivity.kt](../android/app/src/debug/java/com/codexquotatray/android/debug/LiquidBottomTabsFixtureActivity.kt)：
保留上游对照与 production 方案 B，直接观察快 tap、慢 tap、静止长按和 handoff；fixture 使用离线假数据。
不要改写原 production 区来掩盖与基线的差异。

| 场景 | 验收要点 |
| --- | --- |
| 未选中 Tab 的快 tap、较慢 tap、连续反向 tap | 方案 B 随实际距离变化；慢 tap 可能出现高光，不能只挑快 tap 演示 |
| 未选中 Tab 静止按住后松手 | 胶囊接近目标时高光连续增加；按住期间不提交页面，松手才提交 |
| 未选中 Tab 按住后慢拖、立即快拖 | 从当前 preview 亮度连续接管；不跳回、不重放 preview、仅松手提交最近 Tab |
| 选中胶囊 tap、hold、drag | 保持已有 down 高光及原始动画 |
| 拖动消费产生 clickable Cancel | 已接管高光与拖动继续有效 |
| 真正 Cancel、移出、父级滚动接管、外部切页、离开组合 | 无残留亮度、无旧任务重新点亮、无误提交 |
| 多指、快速新一轮按压 | 只允许当前手势所有者驱动；旧 Release/Cancel 不得清除新手势 |
| 明暗主题、LTR/RTL、宽度变化、不同刷新率 | 光斑跟随实际胶囊，检查首次接入和释放，避免错位与闪光 |
| RuntimeShader fallback、动画缩短或关闭、键盘/无障碍点击 | fallback 可用；程序化激活不被误当触摸；明确检查 B 的提前闪光风险 |

在后续实测中对齐 down、接管、首个可见高光帧和 up，使用相同输入节奏逐帧比较。
“接管后无需额外识别等待”与“肉眼无延迟”必须分别判断；当前没有帧时数据，也没有自然度结论。

本轮方案 B 的日常验证遵循 [Android README](../android/README.md)；GUI/真机检查需要明确授权。
