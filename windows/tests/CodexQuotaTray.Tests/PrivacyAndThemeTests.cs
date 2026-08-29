using System.Xml.Linq;
using CodexQuotaTray.Core.Presentation;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class PrivacyAndThemeTests
{
    [TestMethod]
    public void PrototypeDiagnostics_ContainsNoIdentityOrProtocolPayload()
    {
        var diagnostics = PrototypeDiagnostics.Create("0.1.0", "10.0.0");

        StringAssert.Contains(diagnostics, "static demo");
        Assert.IsFalse(diagnostics.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(diagnostics.Contains("email", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(diagnostics.Contains("account_id", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(diagnostics.Contains("jsonrpc", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ThemeDictionaries_ExposeTheSameSemanticResources()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Themes", "Colors.xaml");
        var document = XDocument.Load(file);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionaries = document
            .Descendants(presentation + "ResourceDictionary")
            .Where(element => element.Attribute(xaml + "Key") is not null)
            .ToDictionary(
                element => element.Attribute(xaml + "Key")!.Value,
                element => element.Elements()
                    .Select(resource => resource.Attribute(xaml + "Key")?.Value)
                    .Where(key => key is not null)
                    .ToHashSet(StringComparer.Ordinal));

        Assert.IsTrue(dictionaries.ContainsKey("Light"));
        Assert.IsTrue(dictionaries.ContainsKey("Dark"));
        Assert.IsTrue(dictionaries.ContainsKey("HighContrast"));
        Assert.IsTrue(dictionaries["Light"].Contains("TertiaryTextBrush"));
        CollectionAssert.AreEquivalent(dictionaries["Light"].ToArray(), dictionaries["Dark"].ToArray());
        CollectionAssert.AreEquivalent(dictionaries["Light"].ToArray(), dictionaries["HighContrast"].ToArray());
    }

    [TestMethod]
    public void PanelThemesKeepAcrylicVisibleAndUseNeutralGlassWithBlueHeatmapCells()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Themes", "Colors.xaml");
        var document = XDocument.Load(file);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var themes = document
            .Descendants(presentation + "ResourceDictionary")
            .Where(element => element.Attribute(xaml + "Key")?.Value is "Light" or "Dark")
            .ToDictionary(
                element => element.Attribute(xaml + "Key")!.Value,
                element => element.Elements().ToDictionary(
                    resource => resource.Attribute(xaml + "Key")!.Value,
                    resource => resource.Attribute("Color")?.Value,
                    StringComparer.Ordinal),
                StringComparer.Ordinal);

        Assert.AreEqual("#20FFFFFF", themes["Light"]["MainWindowSurfaceBrush"]);
        Assert.AreEqual("#0F000000", themes["Light"]["TokenHeatmap0Brush"]);
        Assert.AreEqual("#FF155A91", themes["Light"]["TokenHeatmap4Brush"]);
        Assert.AreEqual("#24303438", themes["Dark"]["MainWindowSurfaceBrush"]);
        Assert.AreEqual("#08FFFFFF", themes["Dark"]["TokenHeatmap0Brush"]);
        Assert.AreEqual("#3DB4E6FF", themes["Dark"]["TokenHeatmap1Brush"]);
        Assert.AreEqual("#7A96D2FF", themes["Dark"]["TokenHeatmap2Brush"]);
        Assert.AreEqual("#CC78BEFF", themes["Dark"]["TokenHeatmap3Brush"]);
        Assert.AreEqual("#FF5AAEFF", themes["Dark"]["TokenHeatmap4Brush"]);
        Assert.AreEqual("#FFF3F3F3", themes["Light"]["MainWindowOpaqueSurfaceBrush"]);
        Assert.AreEqual("#FF202020", themes["Dark"]["MainWindowOpaqueSurfaceBrush"]);
        Assert.AreEqual("#FFFFFFFF", themes["Light"]["PanelChromeForegroundBrush"]);
        Assert.AreEqual("#FFFFFFFF", themes["Dark"]["PanelChromeForegroundBrush"]);
    }

    [TestMethod]
    public void HeatmapTooltipUsesAcrylicWindowAndHighContrastFallback()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Themes", "Colors.xaml");
        var document = XDocument.Load(file);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var themes = document
            .Descendants(presentation + "ResourceDictionary")
            .Where(element => element.Attribute(xaml + "Key")?.Value is "Light" or "Dark" or "HighContrast")
            .ToDictionary(
                element => element.Attribute(xaml + "Key")!.Value,
                StringComparer.Ordinal);

        XElement Resource(string theme, string key) => themes[theme]
            .Elements()
            .Single(resource => resource.Attribute(xaml + "Key")?.Value == key);

        var lightFallback = Resource("Light", "TokenHeatmapToolTipFallbackBrush");
        var darkFallback = Resource("Dark", "TokenHeatmapToolTipFallbackBrush");
        var highContrastFallback = Resource("HighContrast", "TokenHeatmapToolTipFallbackBrush");

        Assert.AreEqual("SolidColorBrush", lightFallback.Name.LocalName);
        Assert.AreEqual("SolidColorBrush", darkFallback.Name.LocalName);
        Assert.AreEqual("#FFF5F8FC", lightFallback.Attribute("Color")?.Value);
        Assert.AreEqual("#FF20252B", darkFallback.Attribute("Color")?.Value);
        Assert.AreEqual(
            "#996B7A89",
            Resource("Light", "TokenHeatmapEmptyCellHighlightBrush").Attribute("Color")?.Value);
        Assert.AreEqual(
            "#B8C8D2DC",
            Resource("Dark", "TokenHeatmapEmptyCellHighlightBrush").Attribute("Color")?.Value);
        Assert.AreEqual("SolidColorBrush", highContrastFallback.Name.LocalName);
        Assert.AreEqual(
            "{ThemeResource SystemColorWindowColor}",
            highContrastFallback.Attribute("Color")?.Value);
        Assert.AreEqual(
            "{ThemeResource SystemColorWindowTextColor}",
            Resource("HighContrast", "TokenHeatmapToolTipBorderBrush").Attribute("Color")?.Value);
        Assert.AreEqual(
            "{ThemeResource SystemColorHighlightColor}",
            Resource("HighContrast", "TokenHeatmapEmptyCellHighlightBrush").Attribute("Color")?.Value);
    }

    [TestMethod]
    public void PanelChromeAndTokenLayoutUseTheRequestedPresentationResources()
    {
        var mainWindow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml"));
        var mainWindowCode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml.cs"));
        var settingsWindow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "SettingsWindow.xaml"));
        var settingsWindowCode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "SettingsWindow.xaml.cs"));
        var diagnosticsClipboard = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Services", "DiagnosticsClipboardService.cs"));
        var controls = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Themes", "Controls.xaml"));
        var tokenUsage = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "TokenUsageView.xaml"));
        var tokenUsageCode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "TokenUsageView.xaml.cs"));
        var tooltipWindow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "HeatmapTooltipWindow.xaml"));
        var tooltipWindowCode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "HeatmapTooltipWindow.xaml.cs"));
        var nativeMethods = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Interop", "NativeMethods.cs"));
        var quota = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "QuotaView.xaml"));

        StringAssert.Contains(mainWindow, "ButtonForegroundPointerOver");
        StringAssert.Contains(mainWindow, "Foreground=\"{ThemeResource PanelChromeForegroundBrush}\"");
        StringAssert.Contains(mainWindow, "x:Key=\"ToggleButtonForegroundChecked\" ResourceKey=\"TabSelectedTextBrush\"");
        StringAssert.Contains(mainWindow, "x:Key=\"ToggleButtonForegroundCheckedPointerOver\" ResourceKey=\"TabSelectedTextBrush\"");
        StringAssert.Contains(mainWindow, "x:Key=\"ToggleButtonForegroundCheckedPressed\" ResourceKey=\"TabSelectedTextBrush\"");
        StringAssert.Contains(mainWindow, "Vector3Transition Duration=\"0:0:0.18\"");
        Assert.IsFalse(mainWindow.Contains("ScalarTransition", StringComparison.Ordinal));
        Assert.IsFalse(mainWindowCode.Contains("QuotaTabButton.Foreground", StringComparison.Ordinal));
        Assert.IsFalse(mainWindowCode.Contains("TokenTabButton.Foreground", StringComparison.Ordinal));
        Assert.IsFalse(mainWindowCode.Contains(".Opacity =", StringComparison.Ordinal));
        Assert.IsFalse(mainWindowCode.Contains("incoming.Translation", StringComparison.Ordinal));
        StringAssert.Contains(tokenUsage, "<Grid x:Name=\"TokenUsageRoot\" Margin=\"0,0,0,14\">");
        StringAssert.Contains(tokenUsage, "Padding=\"10,9\"");
        Assert.IsFalse(tokenUsage.Contains("Padding=\"10,9,10,16\"", StringComparison.Ordinal));
        Assert.IsFalse(tokenUsage.Contains("<primitives:Popup", StringComparison.Ordinal));
        Assert.IsFalse(tokenUsage.Contains("SharedHeatmapTooltip", StringComparison.Ordinal));
        Assert.IsFalse(tokenUsage.Contains("SystemBackdropElement", StringComparison.Ordinal));
        Assert.IsFalse(tokenUsage.Contains("AcrylicBrush", StringComparison.Ordinal));
        StringAssert.Contains(tokenUsage, "Vector3Transition Duration=\"0:0:0.08\"");
        Assert.IsFalse(tokenUsage.Contains("ToolTipService.ToolTip", StringComparison.Ordinal));
        StringAssert.Contains(tooltipWindow, "x:Class=\"CodexQuotaTray.App.Views.HeatmapTooltipWindow\"");
        StringAssert.Contains(tooltipWindow, "Width=\"176\"");
        StringAssert.Contains(tooltipWindow, "Height=\"64\"");
        StringAssert.Contains(tooltipWindow, "Background=\"Transparent\"");
        StringAssert.Contains(tooltipWindow, "TokenHeatmapToolTipFallbackBrush");
        StringAssert.Contains(tooltipWindow, "x:Name=\"FallbackSurface\"");
        StringAssert.Contains(tooltipWindow, "x:Name=\"TooltipSurface\"");
        StringAssert.Contains(tooltipWindow, "BorderThickness=\"1\"");
        StringAssert.Contains(tooltipWindow, "CornerRadius=\"9\"");
        StringAssert.Contains(tooltipWindow, "FontSize=\"16\"");
        StringAssert.Contains(tooltipWindow, "FontSize=\"14\"");
        Assert.IsFalse(tooltipWindow.Contains("AcrylicBrush", StringComparison.Ordinal));
        StringAssert.Contains(tokenUsageCode, "TokenHeatmapInteraction.SelectedScale");
        StringAssert.Contains(tokenUsageCode, "new Vector3(TokenHeatmapInteraction.SelectedScale");
        StringAssert.Contains(tokenUsageCode, "CreateHeatmapHighlightBrush(cell.Background, isEmptyCell)");
        StringAssert.Contains(tokenUsageCode, "new Thickness(isEmptyCell ? 1.5 : 1)");
        StringAssert.Contains(tokenUsageCode, "cell.Shadow = new ThemeShadow()");
        StringAssert.Contains(mainWindowCode, "tokenUsageView?.ResetHeatmapInteraction()");
        StringAssert.Contains(mainWindowCode, "tokenUsageView?.PrepareHeatmapInteraction()");
        StringAssert.Contains(mainWindowCode, "tokenUsageView?.Dispose()");
        StringAssert.Contains(tokenUsageCode, "sharedTooltipHasPosition = false");
        StringAssert.Contains(tokenUsageCode, "tokenUsageViewModel.ShowContent");
        StringAssert.Contains(tokenUsageCode, "heatmapInteractionEnabled");
        StringAssert.Contains(tokenUsageCode, "TokenHeatmapInteraction.PlaceTooltipAboveCell");
        StringAssert.Contains(tokenUsageCode, "TransformToVisual(null)");
        StringAssert.Contains(tokenUsageCode, "if (index is not int validIndex)");
        StringAssert.Contains(tokenUsageCode, "if (cell is null || heatmapCell is null)");
        Assert.IsFalse(tokenUsageCode.Contains("DispatcherQueueTimer", StringComparison.Ordinal));
        StringAssert.Contains(tokenUsageCode, "WindowPlacementService.GetWorkArea");
        StringAssert.Contains(tokenUsageCode, "ClientToScreen");
        StringAssert.Contains(tokenUsageCode, "tooltipHeightPixels");
        StringAssert.Contains(tokenUsageCode, "sharedTooltipWindow.SetContent");
        StringAssert.Contains(tokenUsageCode, "sharedTooltipWindow.ShowAt");
        StringAssert.Contains(tokenUsageCode, "GetTooltipScreenPosition");
        StringAssert.Contains(tokenUsageCode, "PointerExitDebounceMilliseconds");
        Assert.IsFalse(tokenUsageCode.Contains("CreateSpringVector3Animation", StringComparison.Ordinal));
        Assert.IsFalse(tokenUsageCode.Contains("tooltipVisual.StopAnimation(\"Offset\")", StringComparison.Ordinal));
        Assert.IsFalse(tokenUsageCode.Contains("sharedTooltipRestingOffset", StringComparison.Ordinal));
        Assert.IsFalse(tokenUsageCode.Contains("tooltipVisual.Offset", StringComparison.Ordinal));
        Assert.IsFalse(tokenUsageCode.Contains("TransformToVisual(HeatmapTooltipOverlay)", StringComparison.Ordinal));
        Assert.IsFalse(tokenUsageCode.Contains("MeasureSharedHeatmapTooltipIfNeeded", StringComparison.Ordinal));
        Assert.IsFalse(tokenUsageCode.Contains("CreateVector3KeyFrameAnimation", StringComparison.Ordinal));
        Assert.IsFalse(tokenUsageCode.Contains("StartAnimation(\"Scale\"", StringComparison.Ordinal));
        StringAssert.Contains(tokenUsageCode, "if (accessibilitySettings.HighContrast)");
        Assert.IsFalse(tokenUsageCode.Contains("new AccessibilitySettings().HighContrast", StringComparison.Ordinal));
        Assert.IsFalse(tokenUsageCode.Contains("SystemBackdropElement", StringComparison.Ordinal));
        Assert.IsFalse(tokenUsageCode.Contains("AcrylicBrush", StringComparison.Ordinal));
        StringAssert.Contains(tooltipWindowCode, "BackdropService");
        StringAssert.Contains(tooltipWindowCode, "backdrop.Apply(this)");
        StringAssert.Contains(tooltipWindowCode, "BackdropKind.DesktopAcrylic");
        StringAssert.Contains(tooltipWindowCode, "NativeMethods.SetWindowPos");
        StringAssert.Contains(tooltipWindowCode, "LogGeometryDiagnostics(\"after-first-show-frame\")");
        StringAssert.Contains(tooltipWindowCode, "NativeMethods.GetWindowRect");
        Assert.IsFalse(tooltipWindowCode.Contains("ExtendsContentIntoTitleBar = true", StringComparison.Ordinal));
        StringAssert.Contains(tooltipWindowCode, "NativeMethods.SwpNoActivate");
        StringAssert.Contains(tooltipWindowCode, "NativeMethods.SwShownoactivate");
        StringAssert.Contains(tooltipWindowCode, "NativeMethods.ConfigureTooltipWindow");
        StringAssert.Contains(tooltipWindowCode, "OverlappedPresenter.CreateForToolWindow");
        StringAssert.Contains(tooltipWindowCode, "SetBorderAndTitleBar(false, false)");
        StringAssert.Contains(tooltipWindowCode, "FallbackSurface.Visibility");
        StringAssert.Contains(tooltipWindowCode, "DwmwaBorderColor");
        StringAssert.Contains(tooltipWindowCode, "DwmColorNone");
        StringAssert.Contains(tooltipWindowCode, "DwmGetWindowAttribute");
        StringAssert.Contains(tooltipWindowCode, "before-first-show");
        StringAssert.Contains(tooltipWindowCode, "after-first-show");
        StringAssert.Contains(tooltipWindowCode, "WmStyleChanging");
        StringAssert.Contains(tooltipWindowCode, "ClearDialogFrameFromStyleChange");
        StringAssert.Contains(tooltipWindowCode, "SwpFrameChanged");
        StringAssert.Contains(tooltipWindowCode, "WmNcHitTest");
        StringAssert.Contains(tooltipWindowCode, "HtTransparent");
        StringAssert.Contains(tooltipWindowCode, "CallWindowProc");
        StringAssert.Contains(tooltipWindowCode, "public void Dispose()");
        StringAssert.Contains(nativeMethods, "WsExToolWindow");
        StringAssert.Contains(nativeMethods, "WsExNoActivate");
        StringAssert.Contains(nativeMethods, "WsExTransparent");
        StringAssert.Contains(nativeMethods, "GwlHwndParent");
        StringAssert.Contains(nativeMethods, "GwlStyle = -16");
        StringAssert.Contains(nativeMethods, "WsDlgFrame = 0x00400000");
        StringAssert.Contains(nativeMethods, "WsBorder = 0x00800000");
        StringAssert.Contains(nativeMethods, "WsThickFrame = 0x00040000");
        StringAssert.Contains(nativeMethods, "WsCaption = 0x00C00000");
        StringAssert.Contains(nativeMethods, "GwlWndProc = -4");
        StringAssert.Contains(nativeMethods, "WmStyleChanging = 0x007C");
        StringAssert.Contains(nativeMethods, "StyleStructNewOffset = sizeof(int)");
        StringAssert.Contains(nativeMethods, "WmNcHitTest = 0x0084");
        StringAssert.Contains(nativeMethods, "HtTransparent = -1");
        StringAssert.Contains(nativeMethods, "DwmwaBorderColor = 34");
        StringAssert.Contains(nativeMethods, "DwmColorNone = unchecked((int)0xFFFFFFFE)");
        StringAssert.Contains(nativeMethods, "DwmGetWindowAttribute");
        StringAssert.Contains(nativeMethods, "GetWindowRect");
        StringAssert.Contains(nativeMethods, "SwpNoMove");
        StringAssert.Contains(nativeMethods, "SwpNoSize");
        StringAssert.Contains(nativeMethods, "SwpNoZOrder");
        StringAssert.Contains(nativeMethods, "SwpFrameChanged");
        StringAssert.Contains(nativeMethods, "SwpNoActivate");
        Assert.IsFalse(tokenUsageCode.Contains("ToolTipService.GetToolTip", StringComparison.Ordinal));
        Assert.IsFalse(tokenUsageCode.Contains("new Vector3(1.28f, 1.28f, 1f)", StringComparison.Ordinal));
        StringAssert.Contains(quota, "Content=\"官方用量\"");
        Assert.IsFalse(quota.Contains("官方用量 ↗", StringComparison.Ordinal));
        StringAssert.Contains(mainWindowCode, "var refreshName = showingTokenPage ? \"刷新统计\" : \"刷新额度\";");
        StringAssert.Contains(tokenUsage, "Text=\"无法刷新统计\"");
        StringAssert.Contains(settingsWindow, "Text=\"保存统计缓存\"");
        StringAssert.Contains(settingsWindow, "IsOn=\"{Binding PersistTokenUsageCache, Mode=TwoWay}\"");
        StringAssert.Contains(controls, "x:Key=\"SettingsActionButtonStyle\" TargetType=\"Button\"");
        StringAssert.Contains(controls, "Property=\"CornerRadius\" Value=\"{ThemeResource ControlCornerRadius}\"");
        StringAssert.Contains(controls, "Property=\"HorizontalContentAlignment\" Value=\"Center\"");
        StringAssert.Contains(mainWindow, "Glyph=\"&#xE7BA;\"");
        StringAssert.Contains(mainWindow, "Text=\"托盘图标初始化失败，请重新启动应用或从窗口退出。\"");
        Assert.IsFalse(mainWindow.Contains("⚠", StringComparison.Ordinal));
        var updateSettingsStart = settingsWindow.IndexOf(
            "<StackPanel x:Name=\"UpdateSettingsPanel\"",
            StringComparison.Ordinal);
        var advancedSettingsStart = settingsWindow.IndexOf(
            "<StackPanel x:Name=\"AdvancedSettingsPanel\"",
            updateSettingsStart,
            StringComparison.Ordinal);
        Assert.IsTrue(updateSettingsStart >= 0);
        Assert.IsTrue(advancedSettingsStart > updateSettingsStart);
        var updateSettings = settingsWindow[updateSettingsStart..advancedSettingsStart];
        Assert.AreEqual(
            2,
            updateSettings.Split("Style=\"{StaticResource SettingsItemCardStyle}\"", StringSplitOptions.None).Length - 1);
        var normalizedSettingsWindow = settingsWindow.Replace("\r\n", "\n");
        var normalizedUpdateSettings = updateSettings.Replace("\r\n", "\n");
        var updateStatusHeaderStart = normalizedUpdateSettings.IndexOf(
            "Text=\"更新状态\" Style=\"{StaticResource SettingsSectionHeaderStyle}\"",
            StringComparison.Ordinal);
        var updateStatusCardStart = normalizedUpdateSettings.IndexOf(
            "<Border Style=\"{StaticResource SettingsItemCardStyle}\">",
            updateStatusHeaderStart,
            StringComparison.Ordinal);
        Assert.IsTrue(updateStatusHeaderStart >= 0);
        Assert.IsTrue(updateStatusCardStart > updateStatusHeaderStart);
        var checkButtonStart = normalizedUpdateSettings.IndexOf(
            "Content=\"检查更新\"",
            updateStatusCardStart,
            StringComparison.Ordinal);
        var checkButtonEnd = normalizedUpdateSettings.IndexOf("/>", checkButtonStart, StringComparison.Ordinal);
        Assert.IsTrue(checkButtonStart >= 0);
        Assert.IsTrue(checkButtonEnd > checkButtonStart);
        StringAssert.Contains(
            normalizedUpdateSettings[checkButtonStart..(checkButtonEnd + 2)],
            "HorizontalAlignment=\"Left\"");
        StringAssert.Contains(normalizedSettingsWindow, "<StackPanel Spacing=\"4\">\n                                <HyperlinkButton Content=\"GitHub 项目主页\"");
        StringAssert.Contains(settingsWindow, "Click=\"OnRegenerateTokenSyncSecretRequested\"");
        StringAssert.Contains(settingsWindow, "Text=\"{Binding TokenSyncEndpointText}\"");
        StringAssert.Contains(settingsWindow, "<Expander");
        StringAssert.Contains(settingsWindow, "x:Name=\"TokenSyncPairingExpander\"");
        StringAssert.Contains(settingsWindow, "x:Name=\"TokenSyncDiagnosticsExpander\"");
        StringAssert.Contains(settingsWindow, "Content=\"复制诊断信息\"");
        StringAssert.Contains(settingsWindow, "Content=\"复制日志信息\"");
        Assert.IsFalse(diagnosticsClipboard.Contains("Clipboard.Flush", StringComparison.Ordinal));
        StringAssert.Contains(diagnosticsClipboard, "catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)");
        StringAssert.Contains(diagnosticsClipboard, "return false;");
        Assert.IsFalse(settingsWindow.Contains("配对密钥默认隐藏", StringComparison.Ordinal));
        Assert.IsFalse(settingsWindow.Contains("用于网络重连后手机无法访问 Windows 的情况", StringComparison.Ordinal));
        StringAssert.Contains(settingsWindow, "Click=\"OnRepairPhoneConnectionRequested\"");
        StringAssert.Contains(settingsWindowCode, "InitializeTokenSyncDisclosure();");
        StringAssert.Contains(settingsWindowCode, "TokenSyncDiagnosticsExpander.IsExpanded = false;");
        StringAssert.Contains(settingsWindowCode, "Title = \"修复手机连接？\"");
        StringAssert.Contains(settingsWindowCode, "PrimaryButtonText = \"确认\"");
        StringAssert.Contains(settingsWindowCode, "CloseButtonText = \"取消\"");
        StringAssert.Contains(settingsWindowCode, "await viewModel.RepairPhoneConnectionCommand.ExecuteAsync(null);");
        StringAssert.Contains(settingsWindowCode, "DefaultHeightDips = 680;");
        StringAssert.Contains(settingsWindowCode, "ResizeSettingsWindowForHome();");
        StringAssert.Contains(settingsWindowCode, "if (showingSettingsHome)");
        StringAssert.Contains(settingsWindowCode, "Title = \"重新生成配对密钥？\"");
        StringAssert.Contains(settingsWindowCode, "Content = \"重新生成后，当前已配对的手机将无法继续连接，需要在手机端重新扫码配对。\"");
        StringAssert.Contains(settingsWindowCode, "PrimaryButtonText = \"重新生成\"");
        StringAssert.Contains(settingsWindowCode, "CloseButtonText = \"取消\"");
        StringAssert.Contains(settingsWindowCode, "DefaultButton = ContentDialogButton.Close");
        StringAssert.Contains(settingsWindowCode, "RequestedTheme = SettingsRoot.ActualTheme");
        StringAssert.Contains(settingsWindowCode, "XamlRoot = SettingsRoot.XamlRoot");
        StringAssert.Contains(settingsWindow, "Click=\"OnLogoutOAuthRequested\"");
        StringAssert.Contains(settingsWindowCode, "Title = \"退出 OAuth 登录？\"");
        StringAssert.Contains(settingsWindowCode, "Content = \"退出后将清除本机保存的 OAuth 登录信息，下次使用 OAuth 数据来源时需要重新登录。\"");
        StringAssert.Contains(settingsWindowCode, "PrimaryButtonText = \"退出登录\"");

        var showIncoming = mainWindowCode.IndexOf("incoming.Visibility = Visibility.Visible;", StringComparison.Ordinal);
        var collapseOutgoing = mainWindowCode.IndexOf(
            "outgoing.Visibility = Visibility.Collapsed;",
            showIncoming,
            StringComparison.Ordinal);
        var switchHeader = mainWindowCode.IndexOf(
            "UpdateHeaderForSelectedPage();",
            collapseOutgoing,
            StringComparison.Ordinal);
        var beginResize = mainWindowCode.IndexOf(
            "AnimatePageHeightAsync(startHeight, targetHeight, revision)",
            switchHeader,
            StringComparison.Ordinal);
        Assert.IsTrue(showIncoming >= 0);
        Assert.IsTrue(collapseOutgoing > showIncoming);
        Assert.IsTrue(switchHeader > collapseOutgoing);
        Assert.IsTrue(beginResize > switchHeader);
    }
}
