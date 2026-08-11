using System.Diagnostics;
using CodexQuotaTray.Core.Presentation;

namespace CodexQuotaTray.App.Services;

internal sealed class ExternalNavigation : IExternalNavigation
{
    private const string OfficialUsageUrl = "https://chatgpt.com/codex/settings/usage";

    public void OpenOfficialUsage()
    {
        Process.Start(new ProcessStartInfo(OfficialUsageUrl) { UseShellExecute = true });
    }
}
