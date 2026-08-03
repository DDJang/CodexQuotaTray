using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Presentation;

namespace CodexQuotaTray.App.Services;

internal sealed class DemoStateProvider : IUiStateProvider, IDiagnosticTextProvider
{
    private int refreshCount;

    public ValueTask<AppUiState> GetSnapshotAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(CreateState());

    public async ValueTask<AppUiState> RefreshAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(650), cancellationToken);
        refreshCount++;
        return CreateState();
    }

    public string CreateDiagnosticText() => PrototypeDiagnostics.Create("0.5.0", Environment.Version.ToString());

    private AppUiState CreateState()
    {
        var now = DateTime.Now;
        var firstRemaining = Math.Max(0, 64 - refreshCount);
        return new AppUiState(
            "Codex",
            "PLUS",
            $"● 示例数据 · 更新于 {now:HH:mm}",
            StatusTone.Success,
            [
                QuotaWindowView.Demo(
                    "5 小时额度",
                    firstRemaining,
                    "4小时后重置",
                    now.AddHours(4).ToString("HH:mm")),
                QuotaWindowView.Demo(
                    "7 天额度",
                    59,
                    "6天后重置",
                    now.AddDays(6).ToString("M月d日 HH:mm")),
            ],
            new ResetCreditViewState(
                ResetCreditKind.PartialDetails,
                2,
                now.AddDays(28)),
            IsPrototype: true);
    }
}
