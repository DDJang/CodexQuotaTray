using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace CodexQuotaTray.Core.Protocol;

public sealed class JsonLineRpcConnection : IAsyncDisposable
{
    private readonly TextReader reader;
    private readonly TextWriter writer;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonRpcResponse>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<RpcServerNotification> notifications = Channel.CreateBounded<RpcServerNotification>(
        new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly Task readTask;
    private readonly object ingressGate = new();
    private readonly HashSet<TaskCompletionSource<bool>> pendingNotificationAcknowledgements = [];
    private long nextId;
    private long nextIngressSequence;
    private long malformedJsonCount;
    private long notificationOverflowSequence;
    private bool notificationOverflowed;
    private TaskCompletionSource<bool>? notificationOverflowSignal;
    private TaskCompletionSource<bool>? notificationOverflowAcknowledgement;
    private bool disposed;

    public JsonLineRpcConnection(TextReader reader, TextWriter writer)
    {
        this.reader = reader;
        this.writer = writer;
        readTask = ReadLoopAsync();
    }

    public long MalformedJsonCount => Interlocked.Read(ref malformedJsonCount);

    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        (await RequestCoreAsync(method, parameters, timeout, cancellationToken, waitForIngressBarrier: false).ConfigureAwait(false)).Payload;

    public Task<JsonRpcResponse> RequestWithSequenceAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RequestWithSequenceAsync(
            method,
            parameters,
            timeout,
            cancellationToken,
            waitForIngressBarrier: true);

    public async Task<JsonRpcResponse> RequestWithSequenceAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool waitForIngressBarrier) =>
        await RequestCoreAsync(method, parameters, timeout, cancellationToken, waitForIngressBarrier).ConfigureAwait(false);

    private async Task<JsonRpcResponse> RequestCoreAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool waitForIngressBarrier)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion))
        {
            throw new CodexClientException(CodexClientErrorKind.Protocol, "Could not register a JSON-RPC request.");
        }

        try
        {
            await WriteAsync(new RpcRequest(id, method, parameters), cancellationToken).ConfigureAwait(false);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            try
            {
                var response = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                if (waitForIngressBarrier)
                {
                    await response.IngressBarrier.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                return response;
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CodexClientException(CodexClientErrorKind.RequestTimeout, "The App Server request timed out.", error);
            }
            catch (OperationCanceledException error)
            {
                throw new CodexClientException(CodexClientErrorKind.Cancelled, "The App Server request was cancelled.", error);
            }
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, CancellationToken cancellationToken) =>
        WriteAsync(new RpcNotification(method), cancellationToken);

    public async IAsyncEnumerable<RpcServerNotification> ReadNotificationsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryTakeOverflow(out var overflow))
            {
                DiscardBufferedNotifications();
                yield return overflow;
                continue;
            }

            if (notifications.Reader.TryRead(out var notification))
            {
                yield return notification;
                continue;
            }

            if (!await WaitForNotificationOrOverflowAsync(cancellationToken).ConfigureAwait(false))
            {
                yield break;
            }
        }
    }

    private async Task<bool> WaitForNotificationOrOverflowAsync(CancellationToken cancellationToken)
    {
        Task overflowWait;
        lock (ingressGate)
        {
            if (notificationOverflowed)
            {
                return true;
            }

            notificationOverflowSignal ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            overflowWait = notificationOverflowSignal.Task;
        }

        using var waitLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var notificationWait = notifications.Reader.WaitToReadAsync(waitLifetime.Token).AsTask();
        var completed = await Task.WhenAny(notificationWait, overflowWait).ConfigureAwait(false);
        waitLifetime.Cancel();
        if (completed == overflowWait)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        return await notificationWait.ConfigureAwait(false);
    }

    private bool TryTakeOverflow(out RpcServerNotification overflow)
    {
        lock (ingressGate)
        {
            if (!notificationOverflowed)
            {
                overflow = null!;
                return false;
            }

            notificationOverflowed = false;
            notificationOverflowSignal = null;
            var pending = notificationOverflowAcknowledgement;
            notificationOverflowAcknowledgement = null;
            Action acknowledgement;
            if (pending is null)
            {
                acknowledgement = static () => { };
            }
            else
            {
                acknowledgement = () => AcknowledgeIngress(pending);
            }
            overflow = RpcServerNotification.Overflow(notificationOverflowSequence, acknowledgement);
            return true;
        }
    }

    private void DiscardBufferedNotifications()
    {
        while (notifications.Reader.TryRead(out var discarded))
        {
            discarded.Acknowledge();
        }
    }

    private async Task WriteAsync<T>(T message, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            throw new CodexClientException(CodexClientErrorKind.TransportClosed, "App Server stdin closed.", error);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(lifetime.Token).ConfigureAwait(false);
                if (line is null)
                {
                    var error = new CodexClientException(CodexClientErrorKind.TransportClosed, "App Server stdout closed.");
                    FailPending(error);
                    notifications.Writer.TryComplete(error);
                    return;
                }

                Dispatch(line);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            var transportError = new CodexClientException(CodexClientErrorKind.TransportClosed, "App Server stdout failed.", error);
            FailPending(transportError);
            notifications.Writer.TryComplete(transportError);
        }
    }

    private void Dispatch(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            Interlocked.Increment(ref malformedJsonCount);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Interlocked.Increment(ref malformedJsonCount);
                return;
            }

            var ingressSequence = Interlocked.Increment(ref nextIngressSequence);

            if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
            {
                if (root.TryGetProperty("method", out var method)
                    && method.ValueKind == JsonValueKind.String
                    && method.GetString() is { Length: > 0 } methodName)
                {
                    var parameters = root.TryGetProperty("params", out var value)
                        ? value.Clone()
                        : default;
                    var notification = new RpcServerNotification(
                        methodName,
                        parameters,
                        ingressSequence,
                        Acknowledgement: string.Equals(
                            methodName,
                            "account/rateLimits/updated",
                            StringComparison.Ordinal)
                            ? RegisterNotificationAcknowledgement()
                            : static () => { });
                    if (!notifications.Writer.TryWrite(notification))
                    {
                        RegisterNotificationOverflow(notification, ingressSequence);
                    }
                }

                return;
            }

            if (!pending.TryRemove(id, out var completion))
            {
                return;
            }

            if (root.TryGetProperty("result", out var result))
            {
                completion.TrySetResult(new JsonRpcResponse(
                    result.Clone(),
                    ingressSequence,
                    PendingNotificationBarrier()));
                return;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var code = error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("code", out var codeValue)
                    && codeValue.TryGetInt64(out var parsed)
                    ? parsed
                    : 0;
                completion.TrySetException(new CodexClientException(
                    code == -32601 ? CodexClientErrorKind.MethodNotFound : CodexClientErrorKind.RemoteError,
                    $"App Server returned error code {code}; server text was suppressed."));
                return;
            }

            completion.TrySetException(new CodexClientException(
                CodexClientErrorKind.Protocol,
                "App Server returned an invalid response envelope."));
        }
    }

    private void FailPending(Exception error)
    {
        foreach (var entry in pending.ToArray())
        {
            if (pending.TryRemove(entry.Key, out var completion))
            {
                completion.TrySetException(error);
            }
        }
    }

    private Action RegisterNotificationAcknowledgement()
    {
        var acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (ingressGate)
        {
            pendingNotificationAcknowledgements.Add(acknowledged);
        }

        return () => AcknowledgeIngress(acknowledged);
    }

    private void RegisterNotificationOverflow(RpcServerNotification notification, long ingressSequence)
    {
        notification.Acknowledge();
        lock (ingressGate)
        {
            notificationOverflowSequence = ingressSequence;
            if (notificationOverflowed)
            {
                return;
            }

            notificationOverflowed = true;
            notificationOverflowAcknowledgement = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pendingNotificationAcknowledgements.Add(notificationOverflowAcknowledgement);
            notificationOverflowSignal ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            notificationOverflowSignal.TrySetResult(true);
        }
    }

    private void AcknowledgeIngress(TaskCompletionSource<bool> acknowledgement)
    {
        acknowledgement.TrySetResult(true);
        lock (ingressGate)
        {
            pendingNotificationAcknowledgements.Remove(acknowledgement);
        }
    }

    private Task PendingNotificationBarrier()
    {
        lock (ingressGate)
        {
            return pendingNotificationAcknowledgements.Count == 0
                ? Task.CompletedTask
                : Task.WhenAll(pendingNotificationAcknowledgements.Select(item => item.Task).ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetime.Cancel();
        notifications.Writer.TryComplete();
        lock (ingressGate)
        {
            foreach (var acknowledgement in pendingNotificationAcknowledgements.ToArray())
            {
                acknowledgement.TrySetResult(true);
            }

            pendingNotificationAcknowledgements.Clear();
            notificationOverflowed = false;
            notificationOverflowSignal?.TrySetResult(true);
            notificationOverflowSignal = null;
            notificationOverflowAcknowledgement = null;
        }
        FailPending(new CodexClientException(CodexClientErrorKind.Cancelled, "The JSON-RPC connection stopped."));
        try
        {
            await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        writeGate.Dispose();
        lifetime.Dispose();
    }

    private sealed record RpcRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] long Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("method")] string Method,
        [property: System.Text.Json.Serialization.JsonPropertyName("params")] object? Params);

    private sealed record RpcNotification(
        [property: System.Text.Json.Serialization.JsonPropertyName("method")] string Method);

}

public sealed record RpcServerNotification(
    string Method,
    JsonElement Parameters,
    long IngressSequence = 0,
    bool IsOverflow = false,
    Action? Acknowledgement = null)
{
    public static RpcServerNotification Overflow(long ingressSequence, Action acknowledgement) =>
        new(string.Empty, default, ingressSequence, IsOverflow: true, Acknowledgement: acknowledgement);

    public void Acknowledge() => Acknowledgement?.Invoke();
}

public sealed record JsonRpcResponse(
    JsonElement Payload,
    long IngressSequence,
    Task IngressBarrier);
