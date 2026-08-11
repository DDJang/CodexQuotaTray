using System.Net.Http.Headers;
using System.Security.Cryptography;
using CodexQuotaTray.Core.TokenUsage;

var scanner = new TokenUsageScanner();
var snapshot = await scanner.ScanAsync(cancellationToken: CancellationToken.None);
Console.WriteLine($"files scanned: {snapshot.FilesScanned}");
Console.WriteLine($"first activity date: {snapshot.FirstActivityDate:yyyy-MM-dd}");
Console.WriteLine($"last activity date: {snapshot.LastActivityDate:yyyy-MM-dd}");
Console.WriteLine($"today tokens: {snapshot.Summary.TodayTokens}");
Console.WriteLine($"7-day tokens: {snapshot.Summary.Last7DaysTokens}");
Console.WriteLine($"30-day tokens: {snapshot.Summary.Last30DaysTokens}");
Console.WriteLine($"lifetime tokens: {snapshot.Summary.LifetimeTokens}");
Console.WriteLine($"peak tokens: {snapshot.Summary.PeakDailyTokens}");
Console.WriteLine($"current streak: {snapshot.Summary.CurrentStreak}");
Console.WriteLine($"longest streak: {snapshot.Summary.LongestStreak}");
Console.WriteLine($"scan elapsed ms: {snapshot.ScanElapsedMilliseconds}");

var address = TokenUsageSyncServer.FindPrivateLanAddress();
if (address is null)
{
    Console.WriteLine("LAN smoke: no private IPv4 address");
    return;
}

var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
await using var server = new TokenUsageSyncServer(scanner, secret);
server.Start(address);
using var client = new HttpClient { BaseAddress = new Uri($"http://{address}:{server.Port}") };
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
var correct = await client.GetAsync("/v1/token-usage");
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "incorrect");
var wrong = await client.GetAsync("/v1/token-usage");
Console.WriteLine($"LAN bind: {address}:{server.Port}");
Console.WriteLine($"correct bearer: {(int)correct.StatusCode}");
Console.WriteLine($"wrong bearer: {(int)wrong.StatusCode}");
