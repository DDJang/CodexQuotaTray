using System.Net;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.Relay;

internal static class PhoneReadGateway
{
    private const int Port = 43902;
    private const string ProbeKey = "0000000000000000000000000000000000000000000000000000000000000000";

    internal static async Task RunAsync(string[] args, string mode)
    {
        if (mode is not ("probe" or "forward"))
        {
            throw new InvalidOperationException("Phone gateway mode must be probe or forward.");
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, Port);
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, int>(_ =>
                RateLimitPartition.GetTokenBucketLimiter(0, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 24,
                    TokensPerPeriod = 12,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    AutoReplenishment = true,
                }));
        });
        using var backend = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        })
        {
            BaseAddress = new Uri("http://127.0.0.1:43901"),
            Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = RemoteSyncSnapshot.MaximumBytes,
        };
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        app.UseRateLimiter();
        app.MapGet("/v1/remote/windows", (HttpRequest request, CancellationToken cancellationToken) =>
            ReadAsync("windows", request, backend, mode, cancellationToken));
        app.MapGet("/v1/remote/ecs", (HttpRequest request, CancellationToken cancellationToken) =>
            ReadAsync("ecs", request, backend, mode, cancellationToken));
        await app.RunAsync();
    }

    private static async Task<IResult> ReadAsync(
        string device,
        HttpRequest request,
        HttpClient backend,
        string mode,
        CancellationToken cancellationToken)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var auth)
            || !auth.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || !RemoteSyncConfiguration.IsValidKey(auth.Parameter ?? ""))
        {
            return Results.Unauthorized();
        }

        if (mode == "probe")
        {
            if (!string.Equals(auth.Parameter, ProbeKey, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }

            var now = DateTimeOffset.UtcNow;
            return Results.Json(new RemoteSyncSnapshot(
                1,
                new QuotaLanSnapshot(1, now, null, "zero_windows", []),
                null));
        }

        using var relayRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/remote/" + device);
        relayRequest.Headers.Authorization = auth;
        try
        {
            using var response = await backend.SendAsync(relayRequest, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => Results.Bytes(
                    await response.Content.ReadAsByteArrayAsync(cancellationToken), "application/json"),
                HttpStatusCode.Unauthorized => Results.Unauthorized(),
                HttpStatusCode.NotFound => Results.NotFound(),
                _ => Results.StatusCode(StatusCodes.Status502BadGateway),
            };
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
    }
}
