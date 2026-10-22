using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>Round 3 fixes: BUG-7 (spoofed X-Forwarded-For), BUG-8 (page overflow), BUG-10 (415 problem+json), BUG-11 (trust-all CIDR splits).</summary>
[Collection(ApiCollection.Name)]
public sealed class SecurityRound3Tests(ApiFixture fx)
{
    private const string StrongKey = "prod-test-key-9f3c1a7e5b2d4c6e8a0b1c2d3e4f5a6b7c8d9e0f";
    private const string ClientIp = "203.0.113.9";

    /// <summary>Simulates a TCP peer address (TestServer has none) before the API's own middleware runs.</summary>
    private sealed class RemoteIpStartupFilter(string ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }

    private ApiFactory RateLimitedFactory(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["RateLimiting:Enabled"] = "true",
            ["RateLimiting:Global:PermitLimit"] = "1000",
            ["RateLimiting:Global:WindowSeconds"] = "60",
            ["RateLimiting:Auth:PermitLimit"] = "2",
            ["RateLimiting:Auth:WindowSeconds"] = "60",
        };
        foreach (var (k, v) in extra)
        {
            settings[k] = v;
        }

        return fx.CreateFactory(settings, s => s.AddTransient<IStartupFilter>(_ => new RemoteIpStartupFilter(ClientIp)));
    }

    private static async Task<HttpStatusCode> LoginWithXffAsync(ApiSession s, string xff) =>
        (await s.SendAsync(HttpMethod.Post, "/api/auth/login", new { email = "nobody@x.local", password = "Wrong1234" }, auth: false,
            configure: r => r.Headers.Add("X-Forwarded-For", xff))).StatusCode;

    [Fact]
    public async Task Spoofed_x_forwarded_for_from_an_untrusted_peer_is_ignored_for_rate_limiting()
    {
        var anon = fx.NewSession(RateLimitedFactory()); // no KnownProxies: 203.0.113.9 is not trusted

        Assert.Equal(HttpStatusCode.Unauthorized, await LoginWithXffAsync(anon, "198.18.0.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginWithXffAsync(anon, "198.18.0.2"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginWithXffAsync(anon, "198.18.0.3")); // same bucket (peer IP)
    }

    [Fact]
    public async Task X_forwarded_for_from_a_known_proxy_selects_the_client_bucket()
    {
        var anon = fx.NewSession(RateLimitedFactory(("ForwardedHeaders:KnownProxies:0", ClientIp)));

        // Positive control: the proxy is trusted, so each forwarded client gets its own bucket.
        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginWithXffAsync(anon, $"198.18.1.{i}"));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await LoginWithXffAsync(anon, "198.18.1.9"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginWithXffAsync(anon, "198.18.1.9"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginWithXffAsync(anon, "198.18.1.9"));
    }

    [Theory]
    [InlineData("/api/stocks?page=2147483647&pageSize=100")]
    [InlineData("/api/invoices?page=2147483647&pageSize=100")]
    [InlineData("/api/stock-trans?page=1000001")]
    public async Task Huge_page_numbers_are_validation_errors_not_500(string url)
    {
        var op = await fx.OperatorAsync();
        var p = await ApiSession.ExpectProblemAsync(await op.GetAsync(url), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.NotNull(p["errors"]!["page"]);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData("application/x-www-form-urlencoded")]
    public async Task Unsupported_media_type_is_problem_json(string contentType)
    {
        var op = await fx.OperatorAsync();
        var res = await op.SendAsync(HttpMethod.Post, "/api/stocks", configure: r =>
            r.Content = new StringContent("code=X", Encoding.UTF8, MediaTypeHeaderValue.Parse(contentType)));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal("UNSUPPORTED_MEDIA_TYPE", json["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("0.0.0.0/1", "128.0.0.0/1")]
    [InlineData("8.0.0.0/8", null)]
    [InlineData("172.0.0.0/8", null)] // wider than 172.16.0.0/12
    [InlineData("::/1", "8000::/1")]
    public void Production_rejects_forwarded_networks_outside_private_ranges(string first, string? second)
    {
        var overrides = new Dictionary<string, string?>
        {
            ["environment"] = "Production",
            ["Jwt:Key"] = StrongKey,
            ["ForwardedHeaders:KnownNetworks:0"] = first,
        };
        if (second is not null)
        {
            overrides["ForwardedHeaders:KnownNetworks:1"] = second;
        }

        var f = fx.CreateFactory(overrides);
        Assert.ThrowsAny<Exception>(() => f.Server);
    }

    [Fact]
    public async Task Production_accepts_a_private_proxy_network()
    {
        var f = fx.CreateFactory(new Dictionary<string, string?>
        {
            ["environment"] = "Production",
            ["Jwt:Key"] = StrongKey,
            ["ForwardedHeaders:KnownNetworks:0"] = "172.30.0.0/24",
            ["ForwardedHeaders:KnownProxies:0"] = "172.30.0.10",
        });
        using var res = await fx.NewSession(f).Client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
