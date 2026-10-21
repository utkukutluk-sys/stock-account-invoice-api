namespace StockAccount.IntegrationTests.Infrastructure;

/// <summary>
/// One migrated + seeded database per test run (collection fixture), dropped at the end. All test classes share it and run
/// sequentially (single collection); each test creates uniquely coded data so tests do not depend on each other.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string AdminEmail = "admin@stockaccount.local";
    public const string AdminPassword = "Admin123!";
    public const string DemoEmail = "demo@stockaccount.local";
    public const string DemoPassword = "Demo123!";
    public const string AhmetEmail = "ahmet@stockaccount.local";
    public const string AhmetPassword = "Ahmet123!";

    private readonly List<ApiFactory> extraFactories = [];
    private readonly Dictionary<string, ApiSession> sessions = new();

    public ApiFactory Factory { get; private set; } = null!;

    public string ConnectionString { get; private set; } = null!;

    public int DemoCompanyId { get; private set; }

    public async Task InitializeAsync()
    {
        ConnectionString = ApiFactory.NewConnectionString();
        Factory = new ApiFactory(ConnectionString);
        _ = Factory.Server; // start host → migrate + seed + approve opening invoice

        var admin = await AdminAsync();
        var companies = await admin.GetJsonAsync("/api/companies?q=Demo");
        DemoCompanyId = companies["items"]![0]!["id"]!.GetValue<int>();
    }

    public async Task DisposeAsync()
    {
        foreach (var f in extraFactories)
        {
            await f.DisposeAsync();
        }

        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (Environment.GetEnvironmentVariable("KEEP_TEST_DB") != "1")
        {
            await ApiFactory.DropDatabaseAsync(ConnectionString);
        }
    }

    /// <summary>Another host on the same database with different settings (e.g. Jwt:RefreshReuseGraceSeconds=0).</summary>
    public ApiFactory CreateFactory(IReadOnlyDictionary<string, string?> overrides)
    {
        var f = new ApiFactory(ConnectionString, overrides);
        extraFactories.Add(f);
        return f;
    }

    public ApiSession NewSession(ApiFactory? factory = null) => new((factory ?? Factory).CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false }));

    public Task<ApiSession> AdminAsync() => CachedLoginAsync(AdminEmail, AdminPassword);

    /// <summary>Admin scoped to the Demo company via X-Company-Id.</summary>
    public async Task<ApiSession> AdminDemoAsync()
    {
        if (sessions.TryGetValue("admin-demo", out var s))
        {
            return s;
        }

        s = await LoginAsync(AdminEmail, AdminPassword);
        s.CompanyId = DemoCompanyId;
        sessions["admin-demo"] = s;
        return s;
    }

    public Task<ApiSession> OperatorAsync() => CachedLoginAsync(DemoEmail, DemoPassword);

    public Task<ApiSession> PortalAsync() => CachedLoginAsync(AhmetEmail, AhmetPassword);

    public async Task<ApiSession> LoginAsync(string email, string password, ApiFactory? factory = null)
    {
        var s = NewSession(factory);
        await s.LoginAsync(email, password);
        return s;
    }

    private async Task<ApiSession> CachedLoginAsync(string email, string password)
    {
        if (!sessions.TryGetValue(email, out var s))
        {
            s = await LoginAsync(email, password);
            sessions[email] = s;
        }

        return s;
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
