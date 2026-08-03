using System.Text.RegularExpressions;
using CodexQuotaTray.Core;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Presentation;
using CodexQuotaTray.Core.Runtime;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class LaunchAndVersionTests
{
    [DataRow("", false, false, "CodexQuotaTray", TrayIdentityMode.Production, true)]
    [DataRow("--demo", true, false, "CodexQuotaTray.Preview", TrayIdentityMode.Preview, false)]
    [DataRow("--isolated-preview-data", false, true, "CodexQuotaTray.Preview", TrayIdentityMode.Preview, false)]
    [DataRow("--demo --isolated-preview-data", true, true, "CodexQuotaTray.Preview", TrayIdentityMode.Preview, false)]
    [DataRow("--shutdown-existing", false, false, "CodexQuotaTray", TrayIdentityMode.Production, true)]
    [DataRow("--shutdown-existing --demo", true, false, "CodexQuotaTray.Preview", TrayIdentityMode.Preview, false)]
    [DataRow("--shutdown-existing --isolated-preview-data", false, true, "CodexQuotaTray.Preview", TrayIdentityMode.Preview, false)]
    [TestMethod]
    public void ArgumentsSelectExpectedRuntimeAndIdentity(
        string arguments,
        bool expectedDemo,
        bool expectedIsolatedPreview,
        string expectedInstanceKey,
        TrayIdentityMode expectedTrayIdentity,
        bool expectedStartupCapability)
    {
        var profile = AppLaunchProfile.FromArguments(
            arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        Assert.AreEqual(expectedDemo, profile.ShowDemo);
        Assert.AreEqual(expectedIsolatedPreview, profile.IsolatedPreview);
        Assert.AreEqual(expectedInstanceKey, profile.InstanceKey);
        Assert.AreEqual(expectedTrayIdentity, profile.TrayIdentity);
        Assert.AreEqual(expectedStartupCapability, profile.CanConfigureStartup);
    }

    [TestMethod]
    public void ActivationArgumentsAlsoSelectPreviewIdentityForDemo()
    {
        var profile = AppLaunchProfile.FromArguments(["CodexQuotaTray.exe"], "--demo");

        Assert.IsTrue(profile.ShowDemo);
        Assert.IsTrue(profile.UsePreviewIdentity);
        Assert.AreEqual(AppLaunchProfile.PreviewInstanceKey, profile.InstanceKey);
        Assert.AreEqual(TrayIdentityMode.Preview, profile.TrayIdentity);
    }

    [DataRow("CodexQuotaTray")]
    [DataRow("CodexQuotaTray Preview")]
    [TestMethod]
    public void DynamicTooltipUsesInjectedIdentityPrefix(string baseName)
    {
        var state = new AppUiState(
            "Codex",
            null,
            "已更新",
            StatusTone.Success,
            [],
            new ResetCreditViewState(ResetCreditKind.Unavailable));

        var tooltip = TrayTooltipFormatter.Create(baseName, state);

        StringAssert.StartsWith(tooltip, $"{baseName} · ");
    }

    [TestMethod]
    public void ProductVersionIsNonEmptySemanticVersion()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(ProductVersion.Current));
        Assert.IsTrue(Regex.IsMatch(
            ProductVersion.Current,
            @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$",
            RegexOptions.CultureInvariant));
        Assert.IsFalse(ProductVersion.Current.Contains('+', StringComparison.Ordinal));
    }
}
