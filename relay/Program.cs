using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexQuotaTray.Core.TokenUsage;
using CodexQuotaTray.Relay;

if (Environment.GetEnvironmentVariable("CODEXQUOTA_RELAY_PHONE_MODE") is { } phoneMode)
{
    await PhoneReadGateway.RunAsync(args, phoneMode);
    return;
}

var keysPath = Environment.GetEnvironmentVariable("CODEXQUOTA_RELAY_KEYS")
    ?? throw new InvalidOperationException("Set CODEXQUOTA_RELAY_KEYS to the relay key file.");
var keys = JsonSerializer.Deserialize<RelayKeys>(
    await File.ReadAllTextAsync(keysPath), JsonSerializerOptions.Web)
    ?? throw new InvalidOperationException("The relay key file is invalid.");
if (!RemoteSyncConfiguration.IsValidKey(keys.Read ?? "")
    || !RemoteSyncConfiguration.IsValidKey(keys.WindowsUpload ?? "")
    || string.Equals(keys.Read, keys.WindowsUpload, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("The relay requires distinct 64-character hexadecimal keys.");
}

var readKey = Hash(keys.Read!);
var uploadKey = Hash(keys.WindowsUpload!);
var directory = Environment.GetEnvironmentVariable("CODEXQUOTA_RELAY_DATA")
    ?? throw new InvalidOperationException("Set CODEXQUOTA_RELAY_DATA to a private data directory.");
var store = new SnapshotStore(directory);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, 43901);
    options.Limits.MaxRequestBodySize = RemoteSyncSnapshot.MaximumBytes;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton(store);
builder.Services.AddHostedService<EcsCollector>();
using var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});

app.MapGet("/v1/remote/{device}", async (string device, HttpRequest request, CancellationToken cancellationToken) =>
{
    if (!Authorized(request, readKey))
    {
        return Results.Unauthorized();
    }
    if (device is not ("windows" or "ecs"))
    {
        return Results.NotFound();
    }

    var snapshot = await store.ReadAsync(device, cancellationToken);
    return snapshot is null ? Results.NotFound() : Results.Json(snapshot);
});

app.MapPut("/v1/remote/windows", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    if (!Authorized(request, uploadKey))
    {
        return Results.Unauthorized();
    }
    if (!request.HasJsonContentType())
    {
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    }

    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(TimeSpan.FromSeconds(12));
    try
    {
        var snapshot = await request.ReadFromJsonAsync<RemoteSyncSnapshot>(deadline.Token);
        if (snapshot is null || !SnapshotStore.IsValid(snapshot))
        {
            return Results.BadRequest();
        }

        await store.MergeAsync("windows", snapshot, deadline.Token);
        return Results.NoContent();
    }
    catch (JsonException)
    {
        return Results.BadRequest();
    }
    catch (InvalidDataException)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status408RequestTimeout);
    }
});

await app.RunAsync();

static byte[] Hash(string key) => SHA256.HashData(Encoding.ASCII.GetBytes(key));

static bool Authorized(HttpRequest request, byte[] expected)
{
    const string prefix = "Bearer ";
    var header = request.Headers.Authorization.ToString();
    return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && RemoteSyncConfiguration.IsValidKey(header[prefix.Length..])
        && CryptographicOperations.FixedTimeEquals(Hash(header[prefix.Length..]), expected);
}

internal sealed record RelayKeys(string? Read, string? WindowsUpload);
