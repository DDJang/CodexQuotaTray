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
        Assert.AreEqual("#FFB4E6FF", themes["Dark"]["TokenHeatmap4Brush"]);
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
        StringAssert.Contains(settingsWindow, "Text=\"保存 Token 用量缓存\"");
        StringAssert.Contains(settingsWindow, "IsOn=\"{Binding PersistTokenUsageCache, Mode=TwoWay}\"");

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
