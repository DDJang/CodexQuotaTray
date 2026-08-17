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
    public void PanelChromeAndTokenLayoutUseTheRequestedPresentationResources()
    {
        var mainWindow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml"));
        var mainWindowCode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml.cs"));
        var settingsWindow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "SettingsWindow.xaml"));
        var settingsWindowCode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "SettingsWindow.xaml.cs"));
        var controls = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Themes", "Controls.xaml"));
        var tokenUsage = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "TokenUsageView.xaml"));
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
        StringAssert.Contains(tokenUsage, "<Grid Margin=\"0,0,0,14\">");
        StringAssert.Contains(tokenUsage, "Padding=\"10,9\"");
        Assert.IsFalse(tokenUsage.Contains("Padding=\"10,9,10,16\"", StringComparison.Ordinal));
        StringAssert.Contains(tokenUsage, "Padding=\"12,8\"");
        StringAssert.Contains(tokenUsage, "FontSize=\"16\"");
        StringAssert.Contains(tokenUsage, "FontSize=\"14\"");
        StringAssert.Contains(tokenUsage, "Vector3Transition Duration=\"0:0:0.12\"");
        var tokenUsageCode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "TokenUsageView.xaml.cs"));
        StringAssert.Contains(tokenUsageCode, "new Vector3(1.28f, 1.28f, 1f)");
        StringAssert.Contains(tokenUsageCode, "new Thickness(2)");
        StringAssert.Contains(tokenUsageCode, "cell.Shadow = new ThemeShadow()");
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
        StringAssert.Contains(settingsWindowCode, "Title = \"重新生成配对密钥？\"");
        StringAssert.Contains(settingsWindowCode, "Content = \"重新生成后，当前已配对的手机将无法继续连接，需要在手机端重新扫码配对。\"");
        StringAssert.Contains(settingsWindowCode, "PrimaryButtonText = \"重新生成\"");
        StringAssert.Contains(settingsWindowCode, "CloseButtonText = \"取消\"");
        StringAssert.Contains(settingsWindowCode, "DefaultButton = ContentDialogButton.Close");
        StringAssert.Contains(settingsWindowCode, "RequestedTheme = SettingsRoot.ActualTheme");
        StringAssert.Contains(settingsWindowCode, "XamlRoot = SettingsRoot.XamlRoot");

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
