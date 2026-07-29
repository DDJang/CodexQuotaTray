using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace CodexQuotaTray.Core.Protocol;

public sealed class JsonLineRpcConnection : IAsyncDisposable
{
    private readonly TextReader reader;
    private readonly TextWriter writer;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<RpcServerNotification> notifications = Channel.CreateBounded<RpcServerNotification>(
        new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        });
    private readonly Task readTask;
    private long nextId;
    private long malformedJsonCount;
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
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
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

    public IAsyncEnumerable<RpcServerNotification> ReadNotificationsAsync(CancellationToken cancellationToken) =>
        notifications.Reader.ReadAllAsync(cancellationToken);

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
                    FailPending(new CodexClientException(CodexClientErrorKind.TransportClosed, "App Server stdout closed."));
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
            FailPending(new CodexClientException(CodexClientErrorKind.TransportClosed, "App Server stdout failed.", error));
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

            if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
            {
                if (root.TryGetProperty("method", out var method)
                    && method.ValueKind == JsonValueKind.String
                    && method.GetString() is { Length: > 0 } methodName)
                {
                    var parameters = root.TryGetProperty("params", out var value)
                        ? value.Clone()
                        : default;
                    _ = notifications.Writer.TryWrite(new RpcServerNotification(methodName, parameters));
                }

                return;
            }

            if (!pending.TryRemove(id, out var completion))
            {
                return;
            }

            if (root.TryGetProperty("result", out var result))
            {
                completion.TrySetResult(result.Clone());
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

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetime.Cancel();
        notifications.Writer.TryComplete();
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

public sealed record RpcServerNotification(string Method, JsonElement Parameters);
