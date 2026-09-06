# Android Bottom Tabs: Reduce Text-Source Chromatic Dispersion

## Scope

Branch for this investigation:

- base: `codex/android-long-press-highlight-investigation`
- investigation branch: `codex/android-chromatic-text-dispersion-investigation`

This document is investigation/design only. It does not change production optics yet.

## Observed problem

The stronger production indicator dispersion (`11dp / 18dp`) produces a richer rainbow edge, which is desirable. However, when the selected tab uses the blue accent content, the indicator also refracts/disperses that blue text/icon source. The result is a visibly colored reflected band near the capsule edge, especially around the selected `统计` content.

Desired result:

- keep the stronger environmental/background edge rainbow;
- reduce the amount of chromatic separation contributed by tab text/icons;
- preserve selected-tab readability and accent brightness;
- do not regress press-preview, drag handoff, highlight timing, geometry, or animation physics.

## Current rendering path

Relevant production path:

`android/app/src/main/java/com/codexquotatray/android/liquidglass/LiquidBottomTabs.kt`

The indicator currently renders from:

```kotlin
backdrop = rememberCombinedBackdrop(backdrop, tabsBackdrop)
```

and applies one lens to the entire combined source:

```kotlin
lens(
    indicatorRefractionHeight.toPx() * progress,
    indicatorRefractionAmount.toPx() * progress,
    chromaticAberration = true,
)
```

The current production defaults are:

```text
refractionHeight = 11dp
refractionAmount = 18dp
```

The `tabsBackdrop` comes from the hidden capture row:

```kotlin
.alpha(0f)
.layerBackdrop(tabsBackdrop)
...
.graphicsLayer(colorFilter = ColorFilter.tint(accentColor))
```

Therefore the selected-tab blue icon/text is part of the same source image that is passed through the chromatic lens.

This is the important root cause:

> the text is not receiving a separate "text chromatic aberration" effect. It is being dispersed because the text is already inside the combined backdrop source before the indicator lens runs.

Increasing the indicator lens from the earlier `10 / 14` to `11 / 18` increases the visibility of this source displacement, so blue glyph content now produces stronger colored ghosting/reflection near the capsule boundary.

## What should NOT be done first

### 1. Do not simply reduce production `11 / 18`

Reducing `refractionAmount` would also weaken the desired environmental rainbow edge. It treats the symptom globally rather than separating the two source types.

Keep `11 / 18` as the baseline during the investigation.

### 2. Do not change highlight, blur, vibrancy, shadow, or velocity physics

The observed colored text reflection is downstream of the combined backdrop + chromatic lens path. It is not caused by `InteractiveHighlight`, `Highlight.Default`, `panelOffset`, or the drag deformation formula.

### 3. Do not fake a cleaner result with an opaque scrim over the text

That would reduce transparency/readability and hide the optical problem rather than fixing the source composition.

## Candidate approaches

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
Option C remains pending user visual acceptance; no production recommendation is recorded yet.

## Performance considerations

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

Do **not** lower `11 / 18` globally as the first fix.

Recommended order:

1. keep `11 / 18` unchanged;
2. first test attenuating only `tabsBackdrop`, with `0.60` as the primary candidate;
3. if selected accent becomes too weak, do not compensate by globally increasing tint/brightness blindly;
4. instead move to a proper two-source/two-pass design where environment dispersion stays strong and selected content is rendered with no or reduced chromatic refraction;
5. choose the simplest solution that passes the fixture without degrading selected text readability or frame behavior.

## Expected final visual contract

The desired final result is:

```text
Environmental/background edges
→ strong, colorful 11/18 chromatic dispersion

Selected icon/text
→ clear blue accent
→ only subtle displacement/refraction
→ no strong glyph-shaped rainbow ribbon at capsule edges
```

This keeps the richer liquid-glass edge discovered in the P1 (`11 / 18`) experiment while preventing UI content itself from becoming the dominant chromatic source.
