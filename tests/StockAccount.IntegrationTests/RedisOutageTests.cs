using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>
/// Redis going down at runtime (CONTRACT §6: "a cache failure never fails a request"; §5.10 health Degraded).
/// Uses a dedicated throw-away Redis container (<c>sa-redis-it</c> on port 6390, never the dev <c>sa-redis</c>) started via the
/// docker CLI. Skipped when docker is not available (set DOCKER_EXE to the docker binary if it is not on PATH).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RedisOutageTests(ApiFixture fx, Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Container = "sa-redis-it";
    private const int Port = 6390;

    internal static string? DockerExe()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOCKER_EXE"),
            "docker",
            @"C:\Program Files\Docker\Docker\resources\bin\docker.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\DockerDesktop\resources\bin\docker.exe"),
        };
        foreach (var c in candidates.Where(c => !string.IsNullOrEmpty(c)))
        {
            try
            {
                if (Run(c!, "version --format {{.Server.Version}}", throwOnError: false).Code == 0)
                {
                    return c;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // not found
            }
        }

        return null;
    }

    private static (int Code, string Output) Run(string exe, string args, bool throwOnError = true)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        if (throwOnError && p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{exe} {args} failed: {output}");
        }

        return (p.ExitCode, output);
    }

    private static string XCache(HttpResponseMessage r) => r.Headers.TryGetValues("X-Cache", out var v) ? v.Single() : "(none)";

    private static async Task<JsonNode> HealthAsync(ApiSession s)
    {
        using var res = await s.Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // Degraded is still 200, only SQL failure is 503
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
    }

    [DockerFact]
    public async Task Api_keeps_serving_when_redis_dies_and_health_reports_degraded()
    {
        var docker = DockerExe()!;

        Run(docker, $"rm -f {Container}", throwOnError: false);
        Run(docker, $"run -d --name {Container} -p {Port}:6379 redis:7-alpine");
        try
        {
            // wait until redis answers
            for (var i = 0; i < 30 && Run(docker, $"exec {Container} redis-cli ping", throwOnError: false).Output.Trim() != "PONG"; i++)
            {
                await Task.Delay(500);
            }

            var f = fx.CreateFactory(new Dictionary<string, string?>
            {
                ["Cache:UseRedis"] = "true",
                ["ConnectionStrings:Redis"] = $"localhost:{Port},abortConnect=false,connectTimeout=1000,syncTimeout=1000,asyncTimeout=1000",
                ["Cache:CircuitBreakSeconds"] = "2", // BUG-6: short break so the recovery phase below stays quick
            });
            var anon = fx.NewSession(f);
            var health = await HealthAsync(anon);
            Assert.Equal("Healthy", health["checks"]!["redis"]!["status"].Str());

            var op = await fx.LoginAsync(ApiFixture.DemoEmail, ApiFixture.DemoPassword, f);
            var stock = await op.CreateStockAsync();
            var url = $"/api/stocks/{stock["id"].Int()}";
            using (var r1 = await op.GetAsync(url)) { Assert.Equal("MISS", XCache(r1)); }
            using (var r2 = await op.GetAsync(url)) { Assert.Equal("HIT", XCache(r2)); }
            var keys = Run(docker, $"exec {Container} redis-cli --scan --pattern sa:stock:*").Output;
            Assert.Contains($"sa:stock:{stock["id"].Int()}", keys);

            // ---- Redis dies at runtime
            Run(docker, $"stop -t 0 {Container}");
            var sw = Stopwatch.StartNew();
            using (var r3 = await op.GetAsync(url))
            {
                output.WriteLine($"first GET by id with Redis down: {sw.ElapsedMilliseconds} ms");
                Assert.Equal(HttpStatusCode.OK, r3.StatusCode);
                Assert.Equal("MISS", XCache(r3)); // served from SQL
            }

            var t = Stopwatch.StartNew();
            var list = await op.GetJsonAsync("/api/stocks?pageSize=5");
            output.WriteLine($"GET list with Redis down: {t.ElapsedMilliseconds} ms");
            Assert.True(t.Elapsed < TimeSpan.FromSeconds(1), $"BUG-6: cached list during outage took {t.ElapsedMilliseconds} ms");
            t.Restart();
            await op.GetJsonAsync(url);
            output.WriteLine($"second GET by id with Redis down: {t.ElapsedMilliseconds} ms");
            Assert.True(t.Elapsed < TimeSpan.FromSeconds(1), $"BUG-6: cached GET by id during outage took {t.ElapsedMilliseconds} ms");
            t.Restart();
            await op.GetJsonAsync("/api/invoices?pageSize=5");
            output.WriteLine($"GET invoices (not cached) with Redis down: {t.ElapsedMilliseconds} ms");
            Assert.NotEmpty(list.Items());
            // writes still work (invalidation errors are swallowed) and reads are fresh
            await op.PutJsonAsync(url, TestData.StockBody(stock["code"].Str(), name: "Redis yok", rowVersion: stock["rowVersion"].Str()));
            Assert.Equal("Redis yok", (await op.GetJsonAsync(url))["name"].Str());
            var approved = await op.PurchaseAsync(stock["id"].Int(), 3);
            Assert.Equal("Approved", approved["status"].Str());
            Assert.Equal(3m, (await op.GetJsonAsync(url))["balance"].Dec());
            output.WriteLine($"outage phase total: {sw.ElapsedMilliseconds} ms");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"degraded requests too slow: {sw.Elapsed}");

            health = await HealthAsync(anon);
            Assert.Equal("Degraded", health["status"].Str());
            Assert.Equal("Degraded", health["checks"]!["redis"]!["status"].Str());
            Assert.Equal("Healthy", health["checks"]!["sql"]!["status"].Str());

            // ---- Redis comes back → cache works again
            Run(docker, $"start {Container}");
            // While the circuit is open the in-memory fallback also answers HIT, so "recovered" means: the entry is in
            // Redis again (written after the breaker's probe flushed the pre-outage keys) and the health check is Healthy.
            var recovered = false;
            for (var i = 0; i < 40 && !recovered; i++)
            {
                await Task.Delay(500);
                using var a = await op.GetAsync(url);
                using var b = await op.GetAsync(url);
                var inRedis = Run(docker, $"exec {Container} redis-cli --scan --pattern sa:stock:{stock["id"].Int()}", throwOnError: false).Output.Contains($"sa:stock:{stock["id"].Int()}");
                recovered = XCache(b) == "HIT" && inRedis
                    && (await HealthAsync(anon))["checks"]!["redis"]!["status"].Str() == "Healthy";
            }

            Assert.True(recovered, "cache did not recover after Redis restart");

            // The pre-outage entry (name before "Redis yok", balance 0) must not come back from Redis (BUG-6 invalidation).
            var after = await op.GetJsonAsync(url);
            Assert.Equal("Redis yok", after["name"].Str());
            Assert.Equal(3m, after["balance"].Dec());
        }
        finally
        {
            Run(docker, $"rm -f {Container}", throwOnError: false);
        }
    }
}

/// <summary>Fact that is skipped when no docker CLI/daemon is available.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(() => RedisOutageTests.DockerExe() is not null);

    public DockerFactAttribute()
    {
        if (!Available.Value)
        {
            Skip = "docker not available (set DOCKER_EXE)";
        }
    }
}
