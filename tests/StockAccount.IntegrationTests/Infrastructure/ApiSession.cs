using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StockAccount.IntegrationTests.Infrastructure;

/// <summary>
/// Thin client over the in-memory API: Bearer token, optional X-Company-Id, and an explicitly managed <c>sa_refresh</c>
/// cookie (handled manually so tests can inspect Set-Cookie attributes and replay old tokens).
/// </summary>
public sealed class ApiSession(HttpClient client)
{
    public const string RefreshCookie = "sa_refresh";

    public HttpClient Client { get; } = client;

    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public int? CompanyId { get; set; }

    public JsonNode? User { get; private set; }

    public async Task<HttpResponseMessage> LoginRawAsync(string email, string password) =>
        await SendAsync(HttpMethod.Post, "/api/auth/login", new { email, password }, auth: false);

    public async Task LoginAsync(string email, string password)
    {
        using var res = await LoginRawAsync(email, password);
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"login {email} failed: {(int)res.StatusCode} {body}");
        var json = JsonNode.Parse(body)!;
        AccessToken = json["accessToken"]!.GetValue<string>();
        User = json["user"];
        RefreshToken = ReadSetCookie(res, RefreshCookie)?.Value;
    }

    public async Task<HttpResponseMessage> RefreshRawAsync(string? cookieValue)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        if (cookieValue is not null)
        {
            req.Headers.Add("Cookie", $"{RefreshCookie}={cookieValue}");
        }

        return await Client.SendAsync(req);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, bool auth = true, Action<HttpRequestMessage>? configure = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            var json = body as string ?? JsonSerializer.Serialize(body, Json.Options);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (auth && AccessToken is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        }

        if (CompanyId is int c)
        {
            req.Headers.Add("X-Company-Id", c.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        configure?.Invoke(req);
        return await Client.SendAsync(req);
    }

    public Task<HttpResponseMessage> GetAsync(string url, Action<HttpRequestMessage>? configure = null) => SendAsync(HttpMethod.Get, url, configure: configure);

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) => SendAsync(HttpMethod.Post, url, body ?? new { });

    public Task<HttpResponseMessage> PutAsync(string url, object body) => SendAsync(HttpMethod.Put, url, body);

    public Task<HttpResponseMessage> DeleteAsync(string url) => SendAsync(HttpMethod.Delete, url);

    public async Task<JsonNode> GetJsonAsync(string url) => await ExpectJsonAsync(await GetAsync(url), HttpStatusCode.OK);

    public async Task<JsonNode> PostJsonAsync(string url, object? body, HttpStatusCode expected) => await ExpectJsonAsync(await PostAsync(url, body), expected);

    public async Task<JsonNode> PutJsonAsync(string url, object body, HttpStatusCode expected = HttpStatusCode.OK) => await ExpectJsonAsync(await PutAsync(url, body), expected);

    public static async Task<JsonNode> ExpectJsonAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync();
            Assert.True(res.StatusCode == expected, $"{res.RequestMessage?.Method} {res.RequestMessage?.RequestUri}: expected {(int)expected} but got {(int)res.StatusCode}: {text}");
            return JsonNode.Parse(text)!;
        }
    }

    /// <summary>Asserts a ProblemDetails response with the given status and code and returns it.</summary>
    public static async Task<JsonNode> ExpectProblemAsync(HttpResponseMessage res, HttpStatusCode status, string code, bool strictContentType = true)
    {
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync();
            Assert.True(res.StatusCode == status, $"{res.RequestMessage?.Method} {res.RequestMessage?.RequestUri}: expected {(int)status} {code} but got {(int)res.StatusCode}: {text}");
            if (strictContentType)
            {
                Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
            }
            var json = JsonNode.Parse(text)!;
            Assert.Equal(code, json["code"]?.GetValue<string>());
            Assert.Equal((int)status, json["status"]!.GetValue<int>());
            Assert.False(string.IsNullOrEmpty(json["correlationId"]?.GetValue<string>()), "correlationId missing");
            Assert.False(string.IsNullOrEmpty(json["traceId"]?.GetValue<string>()), "traceId missing");
            return json;
        }
    }

    public static SetCookie? ReadSetCookie(HttpResponseMessage res, string name)
    {
        if (!res.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        foreach (var raw in values)
        {
            var parts = raw.Split(';', StringSplitOptions.TrimEntries);
            var kv = parts[0].Split('=', 2);
            if (kv[0] != name)
            {
                continue;
            }

            var attrs = parts.Skip(1).Select(p => p.Split('=', 2)).ToDictionary(p => p[0].ToLowerInvariant(), p => p.Length > 1 ? p[1] : "");
            return new SetCookie(kv[0], kv.Length > 1 ? kv[1] : "", raw, attrs);
        }

        return null;
    }
}

public sealed record SetCookie(string Name, string Value, string Raw, IReadOnlyDictionary<string, string> Attributes)
{
    public bool HttpOnly => Attributes.ContainsKey("httponly");

    public string? SameSite => Attributes.GetValueOrDefault("samesite");

    public string? Path => Attributes.GetValueOrDefault("path");

    public bool IsDeletion => Value.Length == 0
        || (Attributes.TryGetValue("expires", out var e) && DateTimeOffset.TryParse(e, out var d) && d < DateTimeOffset.UtcNow);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static decimal Dec(this JsonNode? node) => node!.GetValue<decimal>();

    public static int Int(this JsonNode? node) => node!.GetValue<int>();

    public static string Str(this JsonNode? node) => node!.GetValue<string>();

    public static JsonArray Items(this JsonNode page) => page["items"]!.AsArray();
}
