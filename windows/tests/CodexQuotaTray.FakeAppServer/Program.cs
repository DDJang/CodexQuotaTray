using System.Text.Json;

if (args.Contains("--version", StringComparer.Ordinal))
{
    Console.WriteLine("codex-cli 9.99.0");
    return;
}

var mode = ReadOption(args, "--mode") ?? Environment.GetEnvironmentVariable("CODEX_FAKE_MODE") ?? "happy";
var initialized = false;
while (await Console.In.ReadLineAsync() is { } line)
{
    JsonDocument request;
    try
    {
        request = JsonDocument.Parse(line);
    }
    catch (JsonException)
    {
        continue;
    }

    using (request)
    {
        var root = request.RootElement;
        var method = root.TryGetProperty("method", out var methodNode) ? methodNode.GetString() : null;
        if (method == "initialized")
        {
            initialized = true;
            continue;
        }

        if (!root.TryGetProperty("id", out var id))
        {
            continue;
        }

        if (method == "initialize")
        {
            if (mode == "initialize-timeout")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }

            await WriteAsync(new { id = id.GetInt64(), result = new { userAgent = "codex-cli/9.99.0", platformFamily = "windows", platformOs = "windows" } });
            if (mode == "exit-after-init")
            {
                return;
            }

            continue;
        }

        if (method == "account/read" && initialized && mode != "method-not-found")
        {
            await WriteAsync(new
            {
                id = id.GetInt64(),
                result = new
                {
                    requiresOpenaiAuth = mode == "requires-openai-auth",
                    account = new { type = "chatgpt", email = "account@example.invalid", planType = "plus" },
                },
            });
            continue;
        }

        if (method == "account/usage/read" && initialized && mode != "method-not-found")
        {
            await WriteAsync(new
            {
                id = id.GetInt64(),
                result = new
                {
                    summary = new
                    {
                        lifetimeTokens = 12_345L,
                        peakDailyTokens = 2_345L,
                        currentStreakDays = 3L,
                        longestStreakDays = 8L,
                        longestRunningTurnSec = 42L,
                    },
                    dailyUsageBuckets = new[]
                    {
                        new { startDate = "2026-08-23", tokens = 512L },
                        new { startDate = "2026-08-22", tokens = 256L },
                    },
                },
            });
            continue;
        }

        if (method != "account/rateLimits/read" || !initialized)
        {
            await WriteAsync(new { id = id.GetInt64(), error = new { code = -32601, message = "method not found" } });
            continue;
        }

        if (mode == "read-timeout")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        if (mode == "method-not-found")
        {
            await WriteAsync(new { id = id.GetInt64(), error = new { code = -32601, message = "method not found" } });
            continue;
        }


        if (mode == "malformed")
        {
            Console.WriteLine("{ this is not json");
            await Console.Out.FlushAsync();
        }

        Console.WriteLine("{\"method\":\"future/notification\",\"params\":{\"ignored\":true}}");
        await Console.Out.FlushAsync();
        await WriteAsync(new
        {
            id = id.GetInt64(),
            result = new
            {
                rateLimits = new
                {
                    planType = "plus",
                    primary = new { usedPercent = 28, windowDurationMins = 300, resetsAt = 1_900_000_000L },
                    secondary = new { usedPercent = 43, windowDurationMins = 10_080, resetsAt = 1_900_500_000L },
                },
                rateLimitResetCredits = new
                {
                    availableCount = 2,
                    credits = new[]
                    {
                        new { id = "[REDACTED]", status = "available", expiresAt = 1_901_000_000L },
                        new { id = "[REDACTED-2]", status = "available", expiresAt = 1_902_000_000L },
                    },
                },
            },
        });

        if (mode == "sparse-push")
        {
            await WriteAsync(new
            {
                method = "account/rateLimits/updated",
                @params = new
                {
                    rateLimits = new
                    {
                        primary = new { usedPercent = 31 },
                    },
                },
            });
        }
        else if (mode == "full-push")
        {
            await WriteAsync(new
            {
                method = "account/rateLimits/updated",
                @params = new
                {
                    rateLimits = new
                    {
                        planType = "plus",
                        primary = new { usedPercent = 32, windowDurationMins = 300, resetsAt = 1_900_000_100L },
                    },
                    rateLimitResetCredits = new { availableCount = 1 },
                },
            });
        }

        if (mode == "disconnect-after-read")
        {
            return;
        }
    }
}

static string? ReadOption(string[] values, string name)
{
    var index = Array.IndexOf(values, name);
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

static async Task WriteAsync<T>(T value)
{
    Console.WriteLine(JsonSerializer.Serialize(value));
    await Console.Out.FlushAsync();
}
