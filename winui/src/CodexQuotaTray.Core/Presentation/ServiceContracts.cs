using CodexQuotaTray.Core.Models;

namespace CodexQuotaTray.Core.Presentation;

public interface IUiStateProvider
{
    ValueTask<AppUiState> GetSnapshotAsync(CancellationToken cancellationToken);

    ValueTask<AppUiState> RefreshAsync(CancellationToken cancellationToken);
}

public interface IExternalNavigation
{
    void OpenOfficialUsage();
}

public interface IDiagnosticTextProvider
{
    string CreateDiagnosticText();
}
