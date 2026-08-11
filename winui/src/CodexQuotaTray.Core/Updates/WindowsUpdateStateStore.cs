using CodexQuotaTray.Core.Persistence;

namespace CodexQuotaTray.Core.Updates;

public sealed class FileWindowsUpdateStateStore : IWindowsUpdateStateStore
{
    private readonly JsonFileStore store;
    private readonly string path;

    public FileWindowsUpdateStateStore(JsonFileStore store, string path)
    {
        this.store = store;
        this.path = path;
    }

    public Task<WindowsUpdateState?> LoadAsync(CancellationToken cancellationToken) =>
        store.LoadAsync<WindowsUpdateState>(path, cancellationToken);

    public Task SaveAsync(WindowsUpdateState state, CancellationToken cancellationToken) =>
        store.SaveAsync(path, state, cancellationToken);
}
