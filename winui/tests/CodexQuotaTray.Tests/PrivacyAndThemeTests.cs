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
}
