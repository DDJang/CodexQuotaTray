# Windows 1080p 面板滚轮轻微滚动问题：调查与修复方案

## 背景与现象

在 Windows 端面板中，1920×1080 环境下使用鼠标滚轮上下滚动时，面板主体会发生一小段上下位移；原先的高分辨率环境下没有可感知滚动。

这个现象不是“1080p 下滚轮事件异常”，而是 1080p/当前 DPI 与窗口几何组合让 `ScrollViewer` 出现了很小的正 `ScrollableHeight`，而高分辨率环境下最终布局没有产生这段溢出。

## 调查结论

### 1. 面板当前明确允许纵向滚动

`windows/src/CodexQuotaTray.App/Views/MainWindow.xaml` 中的主体容器为：

```xml
<ScrollViewer
    x:Name="PanelScroller"
    Grid.Row="2"
    VerticalScrollBarVisibility="Auto"
    VerticalScrollMode="Auto"
    HorizontalScrollBarVisibility="Disabled"
    HorizontalScrollMode="Disabled"
    HorizontalContentAlignment="Stretch">
```

因此，只要 WinUI 最终布局后满足：

```text
PanelScroller.ExtentHeight > PanelScroller.ViewportHeight
```

哪怕只多出 1～数个 DIP，鼠标滚轮/触控板都可以改变 `VerticalOffset`，表现为面板内容“只滑动一点距离”。

### 2. 1080p 只是把几何误差暴露出来，不是根因

`MainWindow.xaml.cs` 会根据内容动态计算目标内容高度，例如通过 `MeasureVisibleContentHeight()` 获取 `ContentBottomBoundary` 在 `PanelContent` 内的实际底部位置，并对窗口进行内容适配尺寸调整。

窗口尺寸随后会从逻辑单位转换到实际像素尺寸。现有窗口放置/尺寸代码中存在 DPI 缩放与像素取整路径，例如将逻辑高度乘以 DPI scale 后换算为 client/window pixel size。

因此最终经历的是：

```text
内容实际布局高度（DIP）
→ 目标内容高度（DIP）
→ DPI 换算/像素取整
→ AppWindow 物理像素尺寸
→ WinUI 再布局得到最终 ViewportHeight / ExtentHeight
```

在某些显示器分辨率、缩放比例、工作区尺寸组合下，最终 viewport 可能比内容 extent 少 1～数个 DIP。由于 `PanelScroller` 目前采用 `VerticalScrollMode="Auto"`，这点轻微差值就会被暴露成实际可滚动距离。

高分辨率环境没有现象，只说明该环境下最终 `ScrollableHeight` 为 0 或足够接近 0，不代表实现中不存在同一条风险路径。

### 3. 现有诊断数据可以直接验证

当前代码已经包含 `PanelScroller.ViewportHeight`、`PanelScroller.ExtentHeight` 等布局诊断信息。实现修复前应分别在 1080p 与原高分辨率环境记录：

- `RootLayout.ActualHeight`
- `PanelScroller.ActualHeight`
- `PanelScroller.ViewportHeight`
- `PanelScroller.ExtentHeight`
- `PanelScroller.ScrollableHeight`
- `PanelScroller.VerticalOffset`
- `PanelContent.ActualHeight`
- `PanelContent.DesiredSize.Height`
- `MeasureVisibleContentHeight()` 结果
- 目标内容高度
- 当前 DPI / scale
- 请求的 client pixel size 与最终 client pixel size

预期在有问题的 1080p 环境中能看到一个很小但大于 0 的 `ScrollableHeight`；高分辨率环境则应为 0 或没有形成有效溢出。

## 修复目标

1. 内容本来可以完整放入当前屏幕工作区时，面板不应因为 1～数个 DIP 的布局/取整误差而响应纵向滚轮。
2. 屏幕工作区确实不足、内容确实超过最大可用高度时，仍然保留纵向滚动能力，避免内容被截断。
3. 不通过拦截 `PointerWheelChanged`/鼠标滚轮事件来“遮住”问题。
4. 不通过盲目增大固定窗口高度来规避问题。
5. 避免 `SizeChanged -> resize -> layout -> SizeChanged` 的尺寸反馈循环。

## 推荐修复方案

### 1. 将“窗口高度策略”和“是否允许滚动”统一到同一套几何判断

集中计算三个值：

```text
naturalContentHeight
maxAvailableClientHeight
targetClientHeight
```

其中：

```text
targetClientHeight = min(naturalContentHeight, maxAvailableClientHeight)
```

并由同一处计算：

```text
needsVerticalScroll = naturalContentHeight > maxAvailableClientHeight + tolerance
```

`tolerance` 用于吸收 WinUI layout / DPI / pixel rounding 的微小误差。初始可以用约 `1 DIP` 作为验证候选，但最终值应以 1080p 与高分辨率诊断数据为准，而不是凭经验固定。

### 2. 内容能放下时显式禁用纵向滚动

当 `needsVerticalScroll == false`：

- 将 `PanelScroller.VerticalScrollMode` 设为 `Disabled`
- 将 `VerticalScrollBarVisibility` 设为 `Disabled`（或保持不会出现滚动条的等价状态）
- 将窗口 client height 调整到目标内容高度
- 确保 `PanelScroller.VerticalOffset == 0`；如果此前进入过真正溢出模式并产生过 offset，需要在切回非滚动模式时重置到顶部

这样即使最终布局仍有亚 DIP/1 DIP 级误差，也不会被鼠标滚轮暴露成视觉位移。

### 3. 只有真实受限时才启用滚动

当：

```text
naturalContentHeight > maxAvailableClientHeight + tolerance
```

说明当前工作区确实无法容纳全部内容。此时：

- popup 高度限制在 `maxAvailableClientHeight`
- `PanelScroller.VerticalScrollMode = Auto`
- `VerticalScrollBarVisibility = Auto`

这样仍兼容小屏、较大系统缩放、更多额度卡片或未来内容变长等场景。

### 4. 初始状态建议改为“不滚动”，由代码按真实溢出开启

如果实现走自适应策略，建议 XAML 默认：

```xml
VerticalScrollMode="Disabled"
VerticalScrollBarVisibility="Disabled"
```

窗口布局完成并拿到真实自然高度与工作区上限后，再由统一的 overflow policy 开启 `Auto`。

这样可以避免首次 measure/resize 过程中的短暂溢出让用户提前产生非零 `VerticalOffset`。

### 5. 保留并补强诊断

在 Debug/诊断日志中保留以下差值：

```text
overflow = ExtentHeight - ViewportHeight
```

并记录：

```text
naturalContentHeight
maxAvailableClientHeight
targetClientHeight
needsVerticalScroll
dpiScale
requestedClientSize
actualClientSize
```

修复后应能从日志明确看出：

- 内容 fit：`needsVerticalScroll = false`，滚动禁用，offset = 0
- 内容真实 overflow：`needsVerticalScroll = true`，滚动开启

## 不推荐方案

### 仅拦截鼠标滚轮

不推荐。它只能处理鼠标滚轮这一种输入，还要额外覆盖触控板、键盘、辅助功能等路径，而且根本的 layout overflow 仍存在。

### 仅给窗口多加几个像素

不推荐。不同 DPI、不同显示器、不同系统缩放下误差并不一定相同，而且容易与工作区边界冲突。

### 永久禁用 ScrollViewer

只有在产品明确保证窗口内容在所有支持环境都必然能完整放入工作区时才可采用。当前实现已经使用 `Auto`，更稳妥的方案是保留“真实溢出时可滚动”的 fallback。

## 实现步骤

1. 在 1920×1080 环境抓取现有布局诊断，确认 `ExtentHeight - ViewportHeight` 的实际差值。
2. 在原高分辨率环境抓取同一组数据作为对照。
3. 将 natural content height、可用工作区 client height、target client height 的计算收敛到同一处。
4. 增加 `needsVerticalScroll` 判断与 tolerance。
5. 根据 `needsVerticalScroll` 切换 `PanelScroller` 的 `VerticalScrollMode` / scrollbar visibility。
6. 从 overflow 模式切回 fit 模式时重置 `VerticalOffset` 到 0。
7. 对 resize/layout 更新增加“只有几何值发生有意义变化时才更新”的保护，避免反馈循环。
8. 使用下面的测试矩阵回归。

## 测试矩阵

至少覆盖：

| 场景 | 预期 |
| --- | --- |
| 1920×1080 @ 100% | 内容 fit 时滚轮不移动面板 |
| 1920×1080 @ 125% | 同上；真实 overflow 时仍可滚动 |
| 1920×1080 @ 150% | 同上 |
| 原高分辨率 + 原缩放比例 | 保持现有无滚动行为 |
| 2560×1440 | 无 accidental scroll |
| 3840×2160 | 无 accidental scroll |
| 多显示器、不同 DPI | 在跨屏/重新打开后策略仍正确 |
| 鼠标滚轮 | fit 不动，overflow 可滚 |
| Windows Precision Touchpad | fit 不动，overflow 可滚 |
| 多额度卡片/更长本地化文本 | 超出工作区时内容仍可访问 |
| 重复打开/关闭/刷新 | 不保留异常非零 offset |

## 验收标准

- 在内容能够完整放入工作区的布局中，鼠标滚轮和触控板不会使面板主体发生任何可见纵向位移。
- fit 模式下 `PanelScroller.VerticalOffset == 0`，且不会出现意外滚动条。
- 当内容确实超过当前工作区可用高度时，纵向滚动仍然可用，底部内容不会被截断。
- 1080p 与原高分辨率环境均通过。
- 不引入窗口位置跳动、尺寸反复变化或跨 DPI 显示异常。
