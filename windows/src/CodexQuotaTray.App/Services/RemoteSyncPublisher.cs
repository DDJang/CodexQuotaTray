using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.App.Services;

internal enum RemoteSyncStatus
{
    Loading,
    Disabled,
    WaitingForSnapshot,
    Uploading,
    Connected,
    InvalidKey,
    Unavailable,
    InvalidConfiguration,
}

internal sealed record RemoteSyncPublisherState(
    RemoteSyncConfiguration? Configuration,
    RemoteSyncStatus Status,
    DateTimeOffset? LastSuccessfulUploadUtc = null);

internal sealed class RemoteSyncPublisher(
    RemoteSyncConfigurationStore store,
    Func<QuotaLanSnapshot?> readQuota,
    Func<CancellationToken, Task<TokenUsageSnapshot>> readUsage)
{
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, 1);
    private RemoteSyncPublisherState state = new(null, RemoteSyncStatus.Loading);

    internal event Action? StateChanged;
    internal RemoteSyncPublisherState State => Volatile.Read(ref state);

    internal async Task SaveAsync(RemoteSyncConfiguration? value, CancellationToken cancellationToken)
    {
        await updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = State;
            var lastUpload = value is not null
                && previous.Configuration is { } saved
                && saved.Origin == value.Origin
                && string.Equals(saved.UploadKey, value.UploadKey, StringComparison.Ordinal)
                ? previous.LastSuccessfulUploadUtc
                : null;
            await store.SaveAsync(value, lastUpload, cancellationToken).ConfigureAwait(false);
            UpdateState(new(value, StatusFor(value), lastUpload));
            if (wake.CurrentCount == 0)
            {
                wake.Release();
            }
        }
        finally
        {
            updateGate.Release();
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient(useProxy: true);
        using var tailnetClient = CreateClient(useProxy: false);
        try
        {
            await updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (State.Status == RemoteSyncStatus.Loading)
                {
                    var (configuration, lastUpload) = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                    UpdateState(new(configuration, StatusFor(configuration), lastUpload));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"Remote sync configuration: {error.GetType().Name}");
                UpdateState(new(null, RemoteSyncStatus.InvalidConfiguration));
            }
            finally
            {
                updateGate.Release();
            }

            do
            {
                if (State.Configuration is { Enabled: true } current)
                {
                    var connection = current.Origin.IdnHost.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)
                        ? tailnetClient
                        : client;
                    await PublishAsync(connection, current, cancellationToken).ConfigureAwait(false);
                }
                _ = await wake.WaitAsync(
                    State.Configuration is { Enabled: true } ? PublishInterval : Timeout.InfiniteTimeSpan,
                    cancellationToken).ConfigureAwait(false);
            }
            while (!cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static RemoteSyncStatus StatusFor(RemoteSyncConfiguration? configuration) =>
        configuration is { Enabled: true } ? RemoteSyncStatus.WaitingForSnapshot : RemoteSyncStatus.Disabled;

    private void UpdateStatus(RemoteSyncStatus status) => UpdateState(State with { Status = status });

    private void UpdateState(RemoteSyncPublisherState next)
    {
        if (next != State)
        {
            Volatile.Write(ref state, next);
            StateChanged?.Invoke();
        }
    }

    private static HttpClient CreateClient(bool useProxy) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = useProxy,
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private async Task PublishAsync(
        HttpClient client,
        RemoteSyncConfiguration current,
        CancellationToken cancellationToken)
    {
        TokenUsageSnapshot? usage = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            usage = await readUsage(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine($"Remote sync Token capture: {error.GetType().Name}");
        }

        var snapshot = RemoteSyncSnapshot.Capture(readQuota(), usage);
        await updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(current, State.Configuration))
            {
                return;
            }
            if (snapshot.Quota is null && snapshot.TokenUsage is null)
            {
                UpdateStatus(RemoteSyncStatus.WaitingForSnapshot);
                return;
            }

            UpdateStatus(RemoteSyncStatus.Uploading);
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(current.Origin, "/v1/remote/windows"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.UploadKey);
            request.Content = JsonContent.Create(snapshot);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                var uploadedAt = DateTimeOffset.UtcNow;
                UpdateState(new(current, RemoteSyncStatus.Connected, uploadedAt));
                try
                {
                    await store.SaveAsync(current, uploadedAt, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                {
                    System.Diagnostics.Debug.WriteLine($"Remote sync upload time: {error.GetType().Name}");
                }
                return;
            }

            UpdateStatus(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? RemoteSyncStatus.InvalidKey : RemoteSyncStatus.Unavailable);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine($"Remote sync upload: {error.GetType().Name}");
            UpdateStatus(RemoteSyncStatus.Unavailable);
        }
        finally
        {
            updateGate.Release();
        }
    }
}
