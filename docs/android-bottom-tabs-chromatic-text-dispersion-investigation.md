# Android Bottom Tabs: Reduce Text-Source Chromatic Dispersion

## Scope

Branch for this investigation:

- base: `codex/android-long-press-highlight-investigation`
- investigation branch: `codex/android-chromatic-text-dispersion-investigation`

This document is investigation/design only. It does not change production optics yet.

### 当前结论（2026-09-06 更新）

本轮基于 `4478557062878ad287832daa610c389a87bfb808` 与用户补充截图继续调查。
**A / B / C / D 均已被用户否决，不再按下面的历史实验顺序重试。** 当前没有剩余候选方案，
production 默认渲染路径保持原样。本轮不改变 production 默认渲染路径、依赖或已经验收的长按高光。

目标以用户这次描述为准：**彩虹色散尽量不变，减少胶囊上沿和下沿的蓝色折射/重影**。
不要求把所有文字参与的色散清零，也不能预先认定喜欢的彩虹全部来自环境背景。

## Observed problem

The stronger production indicator dispersion (`11dp / 18dp`) produces a richer rainbow edge, which is desirable. However, when the selected tab uses the blue accent content, the indicator also refracts/disperses that blue text/icon source. The result is a visibly colored reflected band near the capsule edge, especially around the selected `统计` content.

Desired result:

- keep the current stronger edge rainbow, including any contribution from the captured row;
- reduce the upper/lower blue reflected bands without globally removing content dispersion;
- preserve selected-tab readability and accent brightness;
- do not regress press-preview, drag handoff, highlight timing, geometry, or animation physics.

## Current rendering path

Relevant production path:

`android/app/src/main/java/com/codexquotatray/android/liquidglass/LiquidBottomTabs.kt`

The indicator currently renders from:

```kotlin
backdrop = rememberCombinedBackdrop(backdrop, tabsBackdropForIndicator)
```

and applies one lens to the entire combined source:

```kotlin
lens(
    indicatorRefractionHeight.toPx() * progress,
    indicatorRefractionAmount.toPx() * progress,
    chromaticAberration = !useBackgroundChromaticOverlay,
)
```

The current production defaults are:

```text
refractionHeight = 11dp
refractionAmount = 18dp
```

The production caller leaves `tabsBackdropSourceAlpha = 1f` and `useBackgroundChromaticOverlay = false`:
the wrapper returns the original `tabsBackdrop`, and the combined lens remains chromatic.

The `tabsBackdrop` comes from the hidden capture row:

```kotlin
.alpha(0f)
.layerBackdrop(tabsBackdrop)
...
.graphicsLayer(colorFilter = ColorFilter.tint(accentColor))
```

Therefore the selected-tab blue icon/text is part of the same source image that is passed through the chromatic lens.

This establishes source coupling, but not a complete attribution of the visible rainbow:

> the text is not receiving a separate "text chromatic aberration" effect. It is being dispersed because the text is already inside the combined backdrop source before the indicator lens runs.

Increasing the indicator lens from the earlier `10 / 14` to `11 / 18` increases the visibility of this source displacement, so blue glyph content now produces stronger colored ghosting/reflection near the capsule boundary.

The hidden row also contains its glass surface, highlight, and scaled content; it is not a pure glyph
texture. Option B removed that source from the chromatic pass and the user reported losing the rainbow.
This makes the original assumption that background-only dispersion could reproduce the desired rainbow
unsafe. The observation does not, by itself, prove which captured element contributed each color.

## What should NOT be done first

### 1. Do not simply reduce production `11 / 18`

Reducing `refractionAmount` would also alter the desired rainbow edge. It changes the entire optical result
instead of restricting the correction to the unwanted upper/lower blue bands.

Keep `11 / 18` as the baseline during the investigation.

### 2. Do not change highlight, blur, vibrancy, shadow, or velocity physics

Investigate the combined backdrop + lens sampling first. Keep `InteractiveHighlight`, `Highlight.Default`,
`panelOffset`, and drag deformation unchanged so that comparisons isolate the optical correction.
This does not prove that captured highlights or source transforms contribute no color to the final image.

### 3. Do not fake a cleaner result with an opaque scrim over the text

That would reduce transparency/readability and hide the optical problem rather than fixing the source composition.

## Candidate approaches

Historical proposals A–C follow. Their recommendations and parameters describe the experiments already
performed, not the current implementation plan; all three rejection results below remain authoritative.

### Option A — Attenuate only the `tabsBackdrop` contribution before combining

Concept:

```text
base backdrop          100%
tabs/content backdrop   40-70%
        ↓
combined backdrop
        ↓
11 / 18 chromatic lens
```

Use a derived/wrapped backdrop for `tabsBackdrop` (for example with Kyant `rememberBackdrop(...)`) and reduce only that layer's draw contribution before passing it to `rememberCombinedBackdrop`.

Advantages:

- minimal architectural change;
- keeps the same single indicator glass pass;
- keeps background dispersion at full `11 / 18` strength;
- directly reduces how visible blue glyph ghosting is;
- relatively low GPU/regression risk.

Disadvantages:

- because the selected blue content itself currently comes from this captured/tinted layer, attenuating the whole `tabsBackdrop` may also make the selected tab accent look weaker;
- this reduces *visibility/amplitude* of text dispersion, but does not give text a genuinely smaller refraction geometry;
- an alpha/save-layer wrapper may add a small offscreen compositing cost depending on implementation.

Recommended experiment values:

```text
A0  1.00  current baseline
A1  0.75
A2  0.60  recommended first candidate
A3  0.45
```

This is the lowest-risk first experiment, but it should not be promoted to production if selected text loses too much clarity/brightness.

### Option B — Split background glass and selected-content rendering into two optical passes

This is the preferred architecture if Option A cannot preserve selected-content brightness.

Concept:

```text
Pass 1: environment/base backdrop
        → lens 11 / 18
        → chromaticAberration = true
        → keeps rich rainbow edge

Pass 2: selected tab content source
        → no chromatic aberration, or much smaller refraction
        → clipped to the same moving indicator capsule
        → preserves clean blue icon/text
```

The key requirement is that Pass 2 should contain only the selected/tinted tab content (or a source whose transparent regions remain transparent), rather than re-drawing the full glass background and covering Pass 1.

A clean implementation would likely require a second debug/production `LayerBackdrop` that captures only the tab icon/text content, without the full row glass surface. Then the moving indicator can render:

1. the environment glass with strong chromatic dispersion;
2. the selected content overlay with `chromaticAberration = false`, or a deliberately weaker lens.

Suggested content-pass experiments:

```text
B0  no lens, no chromatic aberration
B1  lens 4 / 6, chromaticAberration = false
B2  lens 5 / 8, chromaticAberration = false
```

Advantages:

- independently controls environmental rainbow and glyph distortion;
- selected blue text/icon can remain fully saturated and readable;
- directly matches the desired visual model: strong glass edge dispersion, restrained content dispersion.

Disadvantages:

- more code and one more optical/content composition pass;
- potentially higher GPU cost than Option A;
- requires careful coordinate alignment with LTR/RTL, `panelOffset`, pill value, press scale, and capture-row scale;
- must avoid duplicating the row glass/background in the content-only backdrop.

If implemented, this should be fixture-first and measured before production adoption.

### Option C — Keep combined source but disable chromatic aberration, then add a separate background-only chromatic pass

Concept:

- one background-only chromatic pass for the rainbow;
- one combined non-chromatic pass for current tab rendering.

This is technically possible but is less attractive than Option B because the two full backdrop passes can overlap/cover each other in non-obvious ways and may duplicate background refraction. It is more difficult to reason about alpha and ordering.

Use only if a clean content-only capture cannot be built.

## Recommended investigation sequence

### Phase 1 — Fixture-only source attenuation

Extend the existing chromatic-aberration fixture with a new section dedicated to source composition. Do not change production defaults yet.

Keep:

```text
lens = 11 / 18
chromaticAberration = true
```

Compare:

```text
Tabs source 1.00
Tabs source 0.75
Tabs source 0.60
Tabs source 0.45
```

Test over the same three backdrop modes already used by the fixture:

- multicolor;
- black;
- white.

The multicolor case is the main judge for whether environmental rainbow remains rich. Black/white help determine how much of the observed color is coming from tab content rather than the environment.

Acceptance for Option A:

- selected blue text/icon still looks intentionally selected;
- edge rainbow remains comparable to current `11 / 18`;
- blue glyph-shaped/ribbon-like reflection is visibly reduced;
- no new blur or muddy selected text.

If ~`0.60` produces a clean result without visibly weakening selected content, Option A is the preferred low-risk production solution.

### Phase 1 result — Option A rejected (2026-09-06)

The user acceptance result is that the Option A fixture candidates A0/A1/A2/A3
(`1.00 / 0.75 / 0.60 / 0.45`) are all unacceptable, so the whole `tabsBackdrop` attenuation path
is not promoted to production.

No pixel-by-pixel or frame-time measurements were recorded in this check; this entry records the
visual acceptance result only and does not invent a quantitative threshold. The next experiment is
therefore Option B, keeping the environment pass at `11 / 18` and separating the tab content source.

### Phase 2 — Only if Phase 1 is insufficient: content-only backdrop split

Create a second fixture implementation that separates:

```text
environmentBackdrop
selectedContentBackdrop
```

The environment pass stays:

```text
11 / 18 + chromaticAberration = true
```

The selected-content pass starts with:

```text
no chromatic aberration
```

Then optionally compare a very small non-chromatic refraction (`4/6`, `5/8`) if completely flat content looks visually detached from the glass.

Do not change production until the split path is visually and performance-wise justified.

#### Phase 2 fixture implementation (2026-09-06)

The Debug bottom-tabs fixture now contains the split-source experiment with B0 (no content lens),
B1 (`4 / 6`, non-chromatic), and B2 (`5 / 8`, non-chromatic), each against the same multicolor,
black, and white backdrop modes. The environment pass remains `11 / 18` with chromatic aberration;
the content-only capture has no row glass surface and is rendered as a separate clipped pass.

This is still an investigation fixture, not a production opt-in. Visual and frame-time acceptance
for B remains pending before changing the default production composition.

### Phase 2 result — Option B rejected (2026-09-06)

User acceptance found the split-source Option B path unacceptable: the chromatic dispersion that
was expected to remain on the environment/background pass disappeared completely. Therefore the
B fixture path is rejected and is not promoted to production.

This records the visual result only; no per-frame or pixel measurements were collected. Option C is
the only remaining documented candidate: keep a non-chromatic combined pass and add a separate
background-only chromatic pass, with alpha and pass ordering still requiring fixture validation.

### Phase 3 — Option C fixture implementation (2026-09-06)

The Debug bottom-tabs fixture now contains Option C as an additional experiment. It keeps the
combined background/content source at `11 / 18` with `chromaticAberration = false`, then draws a
separate background-only `11 / 18` chromatic overlay on top. The fixture compares overlay alpha
values `0.20`, `0.35`, `0.50`, and `0.65` over the existing multicolor, black, and white modes.

This is still fixture-only and does not change the production call or its default rendering path.

### Phase 3 result — Option C rejected (2026-09-06)

User acceptance found the Option C composition completely unacceptable: the separate background-only
chromatic overlay made the result directly wrong rather than preserving the intended glass effect.
Therefore Option C is rejected and is not promoted to production.

No per-frame or pixel measurements were collected; this entry records the visual acceptance result
only. Options A, B, and C have now all been rejected. At the end of Phase 3 there was no accepted
candidate; production optics remained unchanged. The subsequent design-only continuation is Option D below.

<a id="option-d"></a>

## 方案 D：保留完整色散，只局部修正蓝色基底

### 1. 为什么不再拆来源或叠玻璃

用户截图显示：选中胶囊上下有彩虹边带，同时有蓝色横向重影。单张截图只能确认外观，
不能区分蓝色来自普通折射、色散偏移、捕获边界还是变换后的重复内容。本轮不将截图中的
无关账户/通知信息写入文档或 fixture。

前三条路径失败后，需要改变的是修正位置：**保留同一个 combined source 和完整的原色散计算，
在 shader 输出前局部修正普通折射参考中的蓝色色偏**。这不再依赖背景单独产生同款彩虹。

| 已否决路径 | 与 D 的区别 |
| --- | --- |
| A：降低整份 tabs source alpha | D 不改变 source alpha、中央蓝字和图标；只修正胶囊上下边带中的部分 RGB |
| B：把文字移出色散通道 | D 保留全部源内容参与七路色散，包括原捕获层的表面、高光与文字 |
| C：无色散 combined + 背景色散叠层 | D 仍只有一个 indicator lens 输出，不叠第二份背景；使用同源数学差值，不使用 overlay alpha |

### 2. 已核实的 shader 接入点

除仓库实现外，本轮直接阅读了当前依赖对应的
[Maven Central 源码包](https://repo.maven.apache.org/maven2/io/github/kyant0/backdrop-android/2.0.0/backdrop-android-2.0.0-sources.jar)，
核对了 `commonMain/com/kyant/backdrop/effects/Lens.kt`、`internal/Shaders.kt`、
`BackdropEffectScope.kt`、`backdrops/CombinedBackdrop.kt` 和 Android 的 RenderEffect 包装。
这里只记录调查基线；项目依赖以 [app/build.gradle.kts](../android/app/build.gradle.kts) 为准。

源码确认：

- `lens` 的公开色散参数是 Boolean，启用时向 shader 设置强度 1；没有“只减少上下蓝色”的公开参数。
- `RoundedRectRefractionWithDispersionShaderString` 先算圆角矩形 SDF、折射距离 `d` 和法线 `grad`，
  再围绕 `q0 = coord + d * grad` 做七路采样。色散偏移与归一化 `x * y` 有关。
- 七路采样的中间项 `green = content.eval(q0)` 实际取得完整 RGBA，只是原合成主要取其 G 通道。
  D 可以复用这份完整样本作为**普通折射参考 U**，不必再画一层非色散玻璃。
- 折射带以外直接返回原 source。D 应保留这个返回路径。

七路位置、权重、alpha 权重、SDF、`11 / 18` 随 press progress 的变化全部保留。
不能仅调低蓝色采样权重，那会直接改变彩虹中的蓝紫部分。

### 3. 先做归因诊断，避免第四次盲试

后续获准实现时，先在同一 fixture 中增加诊断视图，保持字体、布局、背景、按压进度和胶囊位置相同：

| 视图 | 内容 | 决策用途 |
| --- | --- | --- |
| 原始 F | 当前完整七路色散结果 | 唯一视觉基线 |
| 普通折射参考 U | 同源 `content.eval(q0)` | 确认蓝色横带是否在没有色散偏移时已经存在 |
| 有符号差值 E | `F.rgb - U.rgb`，调试显示时映射到中灰，不裁掉负值 | 判断想保留的彩虹和不想要的蓝色是否能在这个分解下区分 |
| 原 source / 捕获边界 | 无新增光学校正，并显示胶囊与 capture bounds | 排查边界采样、裁剪或重复内容；不能把这些问题误当普通蓝色色偏 |

必要时做一次诊断用的 glyph 隐藏/中性色对照，保留隐藏 Row 的玻璃表面和高光，
用于定位贡献；不把该对照当作 production 候选，不以删除文字来验收“色散保留”。

**继续 D 的条件**：蓝色横带在 U 中明显存在，而 E 中仍能辨认目标彩虹差异。
如果蓝色横带主要存在于 E，或实际问题是胶囊外重复绘制/采样边界，停止 D 的调参，
先记录归因；不能宣称一个局部去色公式已经解决这些不同问题。

### 4. 单 shader 的具体计算

以下是设计伪代码，不是当前已提供的库 API。`F` 沿用原 shader；`U` 复用上述中间采样。

```text
F = originalSevenSampleResult(combinedSource)   // 原位置、原权重、原 alpha
U = centerSampleRGBA                           // 已有 content.eval(q0)
E = F.rgb - U.rgb                              // 有符号，不 clamp

N = neutralReferenceWithSameAlpha(U)            // 下述亮度保持的中性参考
W = topBottomBand * accentHueGate * rainbowProtection
k = strength * W

correctedBase = U.rgb + k * (N.rgb - U.rgb)
candidateRGB = correctedBase + E
             = F.rgb + k * (N.rgb - U.rgb)
outputAlpha = F.a
```

实现直接使用最后一行 `F.rgb + correction`，避免先减后加引入不必要的舍入。
`strength == 0`、mask 为零或不在折射带内时，直接返回原 F。

这保留了七路色散采样的空间结构，也在未发生范围限制时保留代数上的差值 E；
**它不等于屏幕上的彩虹颜色逐像素不变**：基底改变、色域限制和人眼对比效应仍会改变观感。
目标是低强度去掉蓝色基底污染，不是把蓝色光全部删除。

`neutralReferenceWithSameAlpha` 的约束：U.a 足够大时先解除预乘，在统一色彩空间中取得亮度，
生成同亮度中性色，再乘回 U.a；U.a 接近零则不校正。不降低透明度，不增加黑色或白色遮罩。
可用 linear sRGB 的亮度系数 `0.2126 / 0.7152 / 0.0722`，转换回工作色彩空间后计算 correction。
原 F 的七路计算不因此切换工作色彩空间。
色彩转换和颜色 uniform 的处理见 [Android AGSL 色彩空间说明](https://developer.android.com/develop/ui/views/graphics/agsl/agsl-vs-glsl#color-spaces)。

### 5. 三重局部约束

**空间 mask：只影响上下折射带。** 使用原 shader 的局部坐标和 SDF，而非屏幕固定 y。
令 `h` 为本帧有效 refraction height，`e = clamp(-sd, 0, h)` 为距边缘的内向距离：

```text
outerGuard = smoothstep(0.10*h, 0.25*h, e)
innerFade  = 1 - smoothstep(0.70*h, h, e)
vertical   = smoothstep(0.65, 0.90, abs(grad.y))
topBottomBand = outerGuard * innerFade * vertical
```

这保留最外侧细边、中央文字区和左右侧边，只在上下边缘内侧平滑接入。`h <= epsilon` 时关闭校正，
避免零除。该比例是首轮实验起点，不是已测得的最佳范围。如果蓝色全在被保护的最外侧，
应如实报告 D 当前 mask 覆盖不到，不能直接扩大至整个胶囊。

**颜色 mask：识别 U 的 accent 色偏，不检查最终 F 是否为蓝色。** 在解除预乘、统一色彩空间后，
令 `v = U.rgb - Y(U)`，`a = accent.rgb - Y(accent)`；以两者归一化点积衡量色相方向接近程度。
可从 `smoothstep(0.80, 0.95, dot(normalize(v), normalize(a)))` 起步，再乘
`smoothstep(0.05, 0.20, length(v) / max(Y(U), epsilon))` 抑制低饱和度区域。
U 或 accent 接近中性时直接取零，避免零向量归一化。accent 从当前主题传入，不硬编码一种蓝。
这不是语义级文字分离：同色背景也可能被匹配，必须在蓝色环境对照中检查误伤。

**彩虹保护：差值较强处保守退出。** 在与 F/U 相同的工作空间计算
`r = length(E) / max(length(U.rgb), epsilon)`，初始可用
`rainbowProtection = 1 - smoothstep(0.10, 0.25, r)`，仅在 alpha 有效的像素参与。
这样更倾向修正普通蓝色重影，保留差值明显的彩虹区域。这里的阈值同样只是候选值；
如果保护后没有可用校正区域，不撤掉保护强行去蓝，而应将其记录为本路径收益不足。

三个 mask 都是连续函数，不增加动画、计时器、按下状态或新的手势入口。

### 6. 有符号差值、alpha 与越界处理

不能把 E 存成普通透明 RGB 叠层：负值会丢失，alpha 混合也不等于上述代数相加。
F、U、N 的 RGB 运算要在同一预乘约定和工作空间下进行，E 不作为独立图层输出。
运行时 shader 的预乘要求见 [Android RuntimeShader](https://developer.android.com/reference/android/graphics/RuntimeShader)。

对首轮 SDR fixture，令 `delta = N.rgb - U.rgb`。可在原 F 已合法时求 k 的最大安全系数，
使每个通道仍在 `[0, F.a]`：正 delta 由 `(F.a - F.channel) / delta.channel` 限制，负 delta 由
`F.channel / -delta.channel` 限制。`kSafe` 取各通道上限与请求 k 的最小值，输出 `F.rgb + kSafe * delta`。
接近零的分量不参与除法；透明或原 F 已越界时保留原 F，并在诊断视图标出。
这样优先减小校正量，而不是先把 E 截成零或最终逐通道硬裁剪，避免破坏彩虹。
宽色域/扩展范围需要另行验证，不能把 SDR 上限直接推广到所有渲染目标。

### 7. 接入位置与最小改动范围

Android 本地 `BottomTabBandLens.kt` 内部包含 D 所需 shader 和 effect 适配；它只通过 Debug 可选入口
启用，默认保持关闭。

- 在 [LiquidBottomTabs.kt](../android/app/src/main/java/com/codexquotatray/android/liquidglass/LiquidBottomTabs.kt)
  的 `combinedIndicatorModifier.effects` 中以 D **替换该次 lens 调用**，不在原 lens 后再跑一遍折射。
- source 仍为 `rememberCombinedBackdrop(backdrop, tabsBackdropForIndicator)`，source alpha 固定使用基线 1；
  `useSplitIndicatorContentSource` 和 `useBackgroundChromaticOverlay` 均保持 false。
- 原始 shader 字符串与 SDF 在库中是 internal。不能假设直接 import 可编译，也不为此升级依赖。
  若复制最小必要源码，应记录本轮核实的 artifact 基线并保留
  [现有第三方许可要求](../THIRD_PARTY_NOTICES.md)。
- `BackdropEffectScope` 提供 shader cache 与可写 `renderEffect`。Android 适配可使用公开 shader
  转换接口与 [createRuntimeShaderEffect](https://developer.android.com/reference/android/graphics/RenderEffect)，
  再转 Compose RenderEffect。不能直接调用库 internal 包装；如存在前置 effect，须保持原链顺序。
- 独立 cache key 缓存 shader，每帧只更新 uniform；复用原 size、offset、corner radii、padding 和
  refraction 参数约定，尤其不能漏掉原 `refractionAmount` 传入 shader 时的符号转换。
- 不改 `indicatorLayerBlock`、隐藏捕获 Row、tab scale、panelOffset、高光、阴影或手势。
  mask 在既有图层变换之前的局部坐标计算，从而随胶囊缩放和移动。
- 不支持 RuntimeShader、参数无效或候选初始化失败时退回原效果；可用
  [InteractiveHighlight.kt](../android/app/src/main/java/com/codexquotatray/android/liquidglass/InteractiveHighlight.kt)
  的平台支持检查作为仓库内参考。回退不改变应用身份或最低 SDK。

以上接口已做源码层面的可行性检查，并已完成 Debug 编译；尚未进行真机 GPU 执行、视觉或性能证明。

### 8. 实验顺序、验收和停止条件

首先完成第 3 节归因和 D0 等价性，之后再比较以下候选；一次只调整 strength：

| 候选 | strength | 用途 |
| --- | --- | --- |
| D0 | 0.00 | 必须与原始 production 渲染等价；先排除 shader 移植、offset 和 effect 链错误 |
| D1 | 0.15 | 保守减弱蓝色色偏 |
| D2 | 0.25 | 首选观察点，不代表推荐 production 参数 |
| D3 | 0.40 | 只用于确定收益/误伤边界，不作为默认 |

已在 [LiquidBottomTabsFixtureActivity.kt](../android/app/src/debug/java/com/codexquotatray/android/debug/LiquidBottomTabsFixtureActivity.kt)
增加独立 D 区与诊断视图，并保留 A/B/C 的失败对照。第一优先级是接近截图的黑底、蓝色“统计”、
相同按住状态；之后检查白底、多色、接近 accent 的蓝底、额度页图标及明暗主题。
锁定相同位置与 progress 对比，再检查完整 tap、hold、慢拖、快拖和释放过程，避免用不同帧解释改善。

验收顺序：

1. **彩虹保留优先**：外侧彩虹的位置、宽度、连续性和红黄绿蓝紫相对关系接近基线。
   如果像 B 一样消失或像 C 一样整体变味，立即淘汰，不以“蓝色减少”抵消失败。
2. **中央内容不动**：中心文字、图标的亮度/颜色/清晰度，以及普通按压高光保持原效果。
3. **上下蓝色减少**：观察的是蓝色横带的色偏和显著程度；不能通过整体变暗、变灰、透明度下降达标。
   D 可能留下较中性的折射轮廓，这是该方案的边界，不承诺消除全部重影几何。
4. **无新伪影**：无灰边、暗洞、彩虹断层、mask 接缝；蓝色环境不被明显误伤。
5. **交互与成本可接受**：长按连续高光、preview → drag、RTL、缩放、快速切页不回归；
   shader 缓存稳定，在同设备同场景记录帧时间。单 shader 不等于零性能成本。

建议把外侧彩虹区、上下内侧蓝带区和中央内容区分别固定为对比 ROI，记录基线与候选差异，
但不在没有测量前写“彩虹保留 95%”之类结论。D 复用了原七路纹理采样，主要增加颜色运算，
这只说明设计没有再加一份完整玻璃 pass，不能据此宣称 GPU 时间不变。

停止条件：D0 不等价、蓝带主要属于 E、保护 mask 后无改善、明显误伤彩虹/蓝色环境、
或范围限制频繁介入。出现任一项就记录失败，不扩大到全局去蓝、不换回 A/B/C，
也不追加固定彩虹贴图来伪造保留结果。若蓝带和彩虹确实共享同一色散结构，
只能接受少量残留蓝色或放宽“彩虹尽量不变”，不能承诺完全独立控制。

本轮实现仍为 fixture-only。常规验证按 [Android README](../android/README.md)，并增加了
strength 有限性/边界测试；保留
[SettingsStructureTest.kt](../android/app/src/test/java/com/codexquotatray/android/SettingsStructureTest.kt)
对 production 默认拓扑和交互的现有检查。GUI、安装与真机比较需另行明确授权。

### Phase 4 — Option D fixture implementation (2026-09-06)

The Debug fixture now includes D0 (the original lens path), D1/D2/D3 local correction strengths,
and U/E/S diagnostic views. D1–D3 replace the combined indicator's single lens call with a cached
public `runtimeShaderEffect` adaptation of the pinned `backdrop:2.0.0` seven-sample shader. The
correction uses the local top/bottom band mask, accent-color gate, signed-difference rainbow
protection, premultiplied-alpha safe scaling, and preserves the original seven sample positions.

D0 intentionally routes through the original library `lens` call for an equivalence baseline. If
RuntimeShader, RenderEffect, shape data, parameters, or shader initialization are unavailable,
the D path falls back to that original lens. No production call opts in to D.

The fixture implementation compiles and the boundary/static checks pass. Visual GPU acceptance,
frame-time comparison, and the D0/D1/D2/D3 visual decision remain pending explicit fixture review.

### Phase 4 result — Option D rejected (2026-09-06)

User visual acceptance found the Option D fixture unacceptable. D0/D1/D2/D3 therefore remain
investigation comparisons only and are not promoted to production.

No per-frame or pixel measurements were collected; this entry records the visual acceptance result
only. Options A, B, C, and D have now all been rejected, so this investigation ends with the
original production combined-source lens path unchanged.

## Performance considerations — historical A–C estimates

The current production indicator uses one combined backdrop lens pass. That remains the cheapest design.

Option A should keep the same main lens pass. A wrapped/attenuated source may introduce a small composition layer, but it is still expected to be much cheaper and lower risk than an additional full liquid-glass pass.

Option B likely adds another backdrop/capture or drawing pass. Before production adoption, compare at least:

- idle frame stability;
- continuous selected-pill drag;
- preview → drag handoff;
- GPU/frame time on the same device used for the current UI validation.

Do not optimize based only on code size; only reject Option B for performance if actual frame behavior regresses.

## Interaction constraints

This investigation must not modify:

- press-preview behavior;
- release-to-commit behavior;
- preview → drag handoff;
- selected-pill direct drag;
- `InteractiveHighlight` timing/shape;
- `DampedDragAnimation` spring values;
- `pressedScale`;
- velocity stretch/squash formula;
- `panelOffset`;
- Bottom Dock width/height/spacing;
- page transition;
- Quota/Token/Settings/About/Modal behavior.

The long-press highlight work in the base branch is independent from this optical-source problem.

## Production recommendation

当前 production 保持原 combined-source 色散路径。A/B/C/D 已否决，不再推荐 source alpha 0.60、
content split、background overlay 或局部蓝色基底修正。调查到此结束，没有剩余候选方案。

本文不构成任何候选已成功的结论；生产默认渲染未启用 A/B/C/D。

## Expected final visual contract

The desired final result is:

```text
Existing combined-source rainbow
→ preserve the current rich 11/18 edge appearance as closely as possible

Selected icon/text
→ clear blue accent
→ unchanged central readability and brightness

Upper/lower blue reflected bands
→ less dominant blue cast, with smooth local correction
→ retain the desired neighboring rainbow instead of removing all content dispersion
```

保留彩虹优先于彻底消灭蓝色。若两者在目标区域不可分离，接受少量残留或重新明确视觉取舍，
不再以“环境色散不变”替代对实际截图效果的验收。
