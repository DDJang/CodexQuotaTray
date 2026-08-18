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

    [TestMethod]
    public void DevelopmentBuildUsesItsOwnIdentityAndStillAllowsStartup()
    {
        var profile = AppLaunchProfile.FromArguments([], isDevelopmentBuild: true);

        Assert.IsFalse(profile.UsePreviewIdentity);
        Assert.AreEqual(AppLaunchProfile.DevelopmentInstanceKey, profile.InstanceKey);
        Assert.AreEqual(TrayIdentityMode.Development, profile.TrayIdentity);
        Assert.IsTrue(profile.CanConfigureStartup);
        Assert.AreNotEqual(AppLaunchProfile.ProductionInstanceKey, profile.InstanceKey);
    }

    [TestMethod]
    public void PreviewArgumentsOverrideTheDevelopmentBuildIdentity()
    {
        var profile = AppLaunchProfile.FromArguments(["CodexQuotaTray.exe", "--isolated-preview-data"], isDevelopmentBuild: true);

        Assert.IsTrue(profile.UsePreviewIdentity);
        Assert.AreEqual(AppLaunchProfile.PreviewInstanceKey, profile.InstanceKey);
        Assert.AreEqual(TrayIdentityMode.Preview, profile.TrayIdentity);
        Assert.IsFalse(profile.CanConfigureStartup);
    }

    [TestMethod]
    public void DynamicTooltipShowsQuotaAndStatusWithoutIdentityPrefix()
    {
        var state = new AppUiState(
            "Codex",
            null,
            "更新于 15:37",
            StatusTone.Success,
            [QuotaWindowView.Demo("7 天额度", 41, "6天后重置", "08-23 15:37")],
            new ResetCreditViewState(ResetCreditKind.Unavailable));

        var tooltip = TrayTooltipFormatter.Create(state);

        Assert.AreEqual("7 天额度 41% · 更新于 15:37", tooltip);
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
