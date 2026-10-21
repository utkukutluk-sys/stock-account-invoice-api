using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>CONTRACT §4.1, §5.4, §11.2 #1: login, cookie refresh rotation, reuse detection + grace, logout, lockout.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthTests(ApiFixture fx)
{
    private const string Password = "Passw0rdX";

    private async Task<(int Id, string Email)> CreateUserAsync(bool isActive = true)
    {
        var admin = await fx.AdminAsync();
        var email = $"it.{TestData.Code("u").ToLowerInvariant()}@test.local";
        var user = await admin.PostJsonAsync("/api/users", new
        {
            email, fullName = "IT Kullanıcı", password = Password, role = "User", companyId = fx.DemoCompanyId, isActive,
        }, HttpStatusCode.Created);
        return (user["id"].Int(), email);
    }

    [Fact]
    public async Task Login_ok_sets_httpOnly_strict_cookie_and_no_refresh_token_in_body()
    {
        var s = fx.NewSession();
        using var res = await s.LoginRawAsync(ApiFixture.AdminEmail, ApiFixture.AdminPassword);
        var text = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = JsonNode.Parse(text)!.AsObject();
        Assert.False(string.IsNullOrEmpty(body["accessToken"]?.GetValue<string>()));
        Assert.EndsWith("Z", body["accessTokenExpiresAt"].Str());
        Assert.False(body.ContainsKey("refreshToken"));
        Assert.DoesNotContain("refresh", string.Join(",", body.Select(p => p.Key)), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Admin", body["user"]!["role"].Str());
        Assert.Null(body["user"]!["companyId"]?.GetValue<int?>());
        Assert.False(body["user"]!["isPortalUser"]!.GetValue<bool>());

        var cookie = ApiSession.ReadSetCookie(res, ApiSession.RefreshCookie);
        Assert.NotNull(cookie);
        Assert.True(cookie.HttpOnly, cookie.Raw);
        Assert.Equal("strict", cookie.SameSite?.ToLowerInvariant());
        Assert.Equal("/", cookie.Path);
        Assert.True(cookie.Attributes.ContainsKey("expires"), cookie.Raw);
        Assert.DoesNotContain(cookie.Value, text); // refresh token value never in the body
    }

    [Fact]
    public async Task Login_invalid_password_and_unknown_email_return_401_invalid_credentials()
    {
        var s = fx.NewSession();
        await ApiSession.ExpectProblemAsync(await s.LoginRawAsync("nobody@stockaccount.local", "Whatever1"), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        var (_, email) = await CreateUserAsync();
        await ApiSession.ExpectProblemAsync(await s.LoginRawAsync(email, "Wrong1234"), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Login_empty_body_is_400()
    {
        var s = fx.NewSession();
        await ApiSession.ExpectProblemAsync(await s.SendAsync(HttpMethod.Post, "/api/auth/login", new { email = "", password = "" }, auth: false), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Me_returns_user_info_per_role()
    {
        var op = await fx.OperatorAsync();
        var me = await op.GetJsonAsync("/api/auth/me");
        Assert.Equal(ApiFixture.DemoEmail, me["email"].Str());
        Assert.Equal("User", me["role"].Str());
        Assert.Equal(fx.DemoCompanyId, me["companyId"].Int());
        Assert.Equal("Demo Gıda A.Ş.", me["companyName"].Str());
        Assert.False(me["isPortalUser"]!.GetValue<bool>());

        var portal = await fx.PortalAsync();
        var pme = await portal.GetJsonAsync("/api/auth/me");
        Assert.True(pme["isPortalUser"]!.GetValue<bool>());
        Assert.Equal("Ahmet Yılmaz", pme["currentAccountTitle"].Str());
    }

    [Fact]
    public async Task Refresh_rotates_cookie_and_returns_new_access_token()
    {
        var s = await fx.LoginAsync(ApiFixture.DemoEmail, ApiFixture.DemoPassword);
        var first = s.RefreshToken!;

        using var res = await s.RefreshRawAsync(first);
        var text = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonNode.Parse(text)!.AsObject();
        Assert.False(body.ContainsKey("refreshToken"));
        var cookie = ApiSession.ReadSetCookie(res, ApiSession.RefreshCookie);
        Assert.NotNull(cookie);
        Assert.NotEqual(first, cookie.Value);
        Assert.True(cookie.HttpOnly);

        // new access token works
        s.AccessToken = body["accessToken"].Str();
        Assert.Equal(ApiFixture.DemoEmail, (await s.GetJsonAsync("/api/auth/me"))["email"].Str());

        // new refresh cookie works again (chain)
        using var res2 = await s.RefreshRawAsync(cookie.Value);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
    }

    [Fact]
    public async Task Replay_within_grace_window_is_401_without_revoking_family()
    {
        var s = await fx.LoginAsync(ApiFixture.DemoEmail, ApiFixture.DemoPassword);
        var old = s.RefreshToken!;
        using var r1 = await s.RefreshRawAsync(old);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var newer = ApiSession.ReadSetCookie(r1, ApiSession.RefreshCookie)!.Value;

        var replay = await s.RefreshRawAsync(old);
        var deleted = ApiSession.ReadSetCookie(replay, ApiSession.RefreshCookie);
        await ApiSession.ExpectProblemAsync(replay, HttpStatusCode.Unauthorized, "INVALID_REFRESH_TOKEN");
        Assert.True(deleted is null || deleted.IsDeletion, "failed refresh must delete the cookie");

        using var r2 = await s.RefreshRawAsync(newer);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode); // family not revoked
    }

    [Fact]
    public async Task Replay_after_grace_window_revokes_family()
    {
        var f = fx.CreateFactory(new Dictionary<string, string?> { ["Jwt:RefreshReuseGraceSeconds"] = "0" });
        var s = await fx.LoginAsync(ApiFixture.DemoEmail, ApiFixture.DemoPassword, f);
        var old = s.RefreshToken!;
        using var r1 = await s.RefreshRawAsync(old);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var newer = ApiSession.ReadSetCookie(r1, ApiSession.RefreshCookie)!.Value;
        await Task.Delay(1100);

        await ApiSession.ExpectProblemAsync(await s.RefreshRawAsync(old), HttpStatusCode.Unauthorized, "INVALID_REFRESH_TOKEN");
        // reuse detected → the new token (same user) is revoked too
        await ApiSession.ExpectProblemAsync(await s.RefreshRawAsync(newer), HttpStatusCode.Unauthorized, "INVALID_REFRESH_TOKEN");

        var reason = await f.ScalarAsync<string>(
            "SELECT TOP 1 RevokedReason FROM RefreshTokens WHERE TokenHash = CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varchar(max), @t)), 2) ",
            new Microsoft.Data.SqlClient.SqlParameter("@t", newer));
        Assert.NotNull(reason);
        Assert.Contains("reuse", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_without_cookie_or_with_garbage_is_401()
    {
        var s = fx.NewSession();
        await ApiSession.ExpectProblemAsync(await s.RefreshRawAsync(null), HttpStatusCode.Unauthorized, "INVALID_REFRESH_TOKEN");
        await ApiSession.ExpectProblemAsync(await s.RefreshRawAsync("not-a-real-token"), HttpStatusCode.Unauthorized, "INVALID_REFRESH_TOKEN");
    }

    [Fact]
    public async Task Refresh_and_logout_require_json_content_type()
    {
        var s = await fx.LoginAsync(ApiFixture.DemoEmail, ApiFixture.DemoPassword);
        foreach (var path in new[] { "/api/auth/refresh", "/api/auth/logout" })
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("x=1", Encoding.UTF8, "application/x-www-form-urlencoded") };
            req.Headers.Add("Cookie", $"{ApiSession.RefreshCookie}={s.RefreshToken}");
            using var res = await s.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
        }

        // GET is not allowed either
        using var get = await s.Client.GetAsync("/api/auth/refresh");
        Assert.True(get.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, get.StatusCode.ToString());
    }

    [Fact]
    public async Task Logout_deletes_cookie_and_refresh_afterwards_fails()
    {
        var s = await fx.LoginAsync(ApiFixture.DemoEmail, ApiFixture.DemoPassword);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        req.Headers.Add("Cookie", $"{ApiSession.RefreshCookie}={s.RefreshToken}");
        using var res = await s.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var cookie = ApiSession.ReadSetCookie(res, ApiSession.RefreshCookie);
        Assert.NotNull(cookie);
        Assert.True(cookie.IsDeletion, cookie.Raw);

        await ApiSession.ExpectProblemAsync(await s.RefreshRawAsync(s.RefreshToken), HttpStatusCode.Unauthorized, "INVALID_REFRESH_TOKEN");

        // idempotent: logout without cookie → 204
        using var again = await s.SendAsync(HttpMethod.Post, "/api/auth/logout", new { }, auth: false);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task Lockout_after_five_failures()
    {
        var (_, email) = await CreateUserAsync();
        var s = fx.NewSession();
        for (var i = 0; i < 5; i++)
        {
            using var r = await s.LoginRawAsync(email, "Wrong1234");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        await ApiSession.ExpectProblemAsync(await s.LoginRawAsync(email, Password), HttpStatusCode.Unauthorized, "ACCOUNT_LOCKED");
    }

    [Fact]
    public async Task Inactive_user_cannot_login_and_deactivation_revokes_refresh()
    {
        var (_, inactiveEmail) = await CreateUserAsync(isActive: false);
        await ApiSession.ExpectProblemAsync(await fx.NewSession().LoginRawAsync(inactiveEmail, Password), HttpStatusCode.Unauthorized, "USER_INACTIVE");

        var (id, email) = await CreateUserAsync();
        var user = await fx.LoginAsync(email, Password);
        var admin = await fx.AdminAsync();
        await admin.PutJsonAsync($"/api/users/{id}", new { fullName = "IT Kullanıcı", role = "User", companyId = fx.DemoCompanyId, isActive = false });
        await ApiSession.ExpectProblemAsync(await user.RefreshRawAsync(user.RefreshToken), HttpStatusCode.Unauthorized, "INVALID_REFRESH_TOKEN");
    }

    [Fact]
    public async Task Change_password_works_and_validates_current_password()
    {
        var (_, email) = await CreateUserAsync();
        var s = await fx.LoginAsync(email, Password);

        await ApiSession.ExpectProblemAsync(
            await s.PostAsync("/api/auth/change-password", new { currentPassword = "Wrong1234", newPassword = "NewPassw0rd" }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ApiSession.ExpectProblemAsync(
            await s.PostAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = "short" }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        using var ok = await s.PostAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = "NewPassw0rd" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        await ApiSession.ExpectProblemAsync(await fx.NewSession().LoginRawAsync(email, Password), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        await fx.LoginAsync(email, "NewPassw0rd");
    }

    [Fact]
    public async Task Tampered_or_missing_bearer_is_401_problem()
    {
        var s = fx.NewSession();
        s.AccessToken = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.invalid";
        await ApiSession.ExpectProblemAsync(await s.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }
}
