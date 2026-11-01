using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agropecuaria.Contracts;
using Agropecuaria.Application;
using Agropecuaria.Infrastructure;
using System.Security.Cryptography;
using Xunit;

namespace Agropecuaria.Tests;

// Real process/HTTP/SQLite test. Uses isolated data; never reads the developer's local.db.
public sealed class ApiProcessTests
{
    private readonly int apiPort = FreePort();
    private readonly int receiverPort = FreePort();
    private const string Master = "HTTP test master passphrase only";
    private const string AdminPassword = "HTTP administrator password 123!";
    [Fact]
    public async Task RealApiSurvivesReceiverOutageAndRestartThenDrainsOutbox()
    {
        var root = FindRoot();
        var workspace = Path.Combine(Path.GetTempPath(), "agropecuaria-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Process? api = null; Process? receiver = null;
        try
        {
            using (var init = Start(root, workspace, false, "--init-dev"))
            {
                await WaitForExit(init); Assert.Equal(0, init.ExitCode);
            }
            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace, ".data", "development.json")));
            Provision(workspace, settings);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}/"), Timeout = TimeSpan.FromSeconds(2) };
            using var remote = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{receiverPort}/"), Timeout = TimeSpan.FromSeconds(2) };
            remote.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.RootElement.GetProperty("Sync").GetProperty("AccessToken").GetString());
            api = Start(root, workspace, false);
            await WaitUntil(async () => (await http.GetAsync("health")).IsSuccessStatusCode);
            using var login = await http.PostAsJsonAsync("api/auth/login", new LoginRequest("owner", AdminPassword));
            login.EnsureSuccessStatusCode();
            var session = (await login.Content.ReadFromJsonAsync<LoginResult>())!;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using (var unauthorized = new HttpClient { BaseAddress = http.BaseAddress })
                Assert.Equal(HttpStatusCode.Unauthorized, (await unauthorized.GetAsync("api/products")).StatusCode);

            var productId = Guid.NewGuid();
            using var product = await http.PostAsJsonAsync("api/products", new CreateProductRequest(Guid.NewGuid(), productId,
                "Prueba HTTP", "Weight", "Quintal", 5, [new("Pound", 500)]));
            product.EnsureSuccessStatusCode();
            var sale = new CreateSaleRequest(Guid.NewGuid(), [new(productId, "Pound", 18)]);
            using var response = await http.PostAsJsonAsync("api/sales", sale); response.EnsureSuccessStatusCode();
            var receipt = await response.Content.ReadFromJsonAsync<SaleResult>(); Assert.NotNull(receipt);
            var stock = await http.GetFromJsonAsync<List<ProductView>>("api/products");
            Assert.Equal(482_000_000, Assert.Single(stock!).StockBase);
            await WaitUntil(async () => (await http.GetFromJsonAsync<SyncStatus>("api/sync/status"))!.State == "Offline");

            Stop(api); api.Dispose(); api = Start(root, workspace, false);
            await WaitUntil(async () => (await http.GetAsync("health")).IsSuccessStatusCode);
            var pending = await http.GetFromJsonAsync<SyncStatus>("api/sync/status"); Assert.Equal(2, pending!.PendingCount);
            using var replay = await http.PostAsJsonAsync("api/sales", sale); replay.EnsureSuccessStatusCode();
            Assert.Equal(receipt, await replay.Content.ReadFromJsonAsync<SaleResult>());

            receiver = Start(root, workspace, true);
            await WaitUntil(async () => (await remote.GetAsync("health")).IsSuccessStatusCode);
            await WaitUntil(async () =>
            {
                var status = await http.GetFromJsonAsync<SyncStatus>("api/sync/status");
                return status!.PendingCount == 0 && status.State == "UpToDate";
            });
            using var remoteHealth = await remote.GetFromJsonAsync<JsonDocument>("health");
            Assert.Equal(2, remoteHealth!.RootElement.GetProperty("events").GetInt64());
        }
        finally
        {
            if (receiver != null) { Stop(receiver); receiver.Dispose(); }
            if (api != null) { Stop(api); api.Dispose(); }
            Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public async Task RestRolesTemporaryPasswordsAndRevocationWorkWithoutAnyFrontend()
    {
        var root = FindRoot(); var workspace = Path.Combine(Path.GetTempPath(), "agropecuaria-roles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace); Process? api = null;
        try
        {
            using (var init = Start(root, workspace, false, "--init-dev")) { await WaitForExit(init); Assert.Equal(0, init.ExitCode); }
            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace, ".data", "development.json")));
            Provision(workspace, settings); api = Start(root, workspace, false);
            using var admin = Client(); await WaitUntil(async () => (await admin.GetAsync("health")).IsSuccessStatusCode);
            using var anonymous = Client();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/admin/users")).StatusCode);
            anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.RootElement.GetProperty("LocalApi").GetProperty("AccessToken").GetString());
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/products")).StatusCode);
            await Login(admin, "owner", AdminPassword);
            var me = await admin.GetFromJsonAsync<UserView>("api/auth/me");
            var lastAdmin = await admin.PatchAsJsonAsync($"api/admin/users/{me!.Id}", new UpdateUserRequest("Viewer", true));
            Assert.Equal(HttpStatusCode.Conflict, lastAdmin.StatusCode);
            var productId = Guid.NewGuid();
            using var product = await admin.PostAsJsonAsync("api/products", new CreateProductRequest(Guid.NewGuid(), productId,
                "Product", "Count", "Piece", 10, [new("Piece", 100)])); product.EnsureSuccessStatusCode();
            using var create = await admin.PostAsJsonAsync("api/admin/users", new CreateUserRequest("cashier", "Cashier", "Cashier", AdminPassword));
            create.EnsureSuccessStatusCode(); var user = (await create.Content.ReadFromJsonAsync<UserView>())!;
            using var cashier = Client(); await Login(cashier, "cashier", AdminPassword);
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("api/products")).StatusCode);
            const string changedPassword = "The cashier personal password 456!";
            using var change = await cashier.PostAsJsonAsync("api/auth/password", new ChangePasswordRequest(AdminPassword, changedPassword));
            Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.GetAsync("api/auth/me")).StatusCode);
            await Login(cashier, "cashier", changedPassword);
            Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("api/products")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("api/admin/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("api/admin/users", new CreateUserRequest("attacker", "Attacker", "Admin", changedPassword))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PatchAsJsonAsync($"api/admin/users/{user.Id}", new UpdateUserRequest("Admin", true))).StatusCode);
            using var sale = await cashier.PostAsJsonAsync("api/sales", new CreateSaleRequest(Guid.NewGuid(), [new(productId, "Piece", 1)]));
            sale.EnsureSuccessStatusCode();
            using var auditResponse = await admin.GetAsync("api/admin/audit"); auditResponse.EnsureSuccessStatusCode();
            var audit = (await auditResponse.Content.ReadFromJsonAsync<List<AuditView>>())!;
            Assert.Contains(audit, e => e.Action == "SaleCompleted" && e.ActorId == user.Id);
            using var update = await admin.PatchAsJsonAsync($"api/admin/users/{user.Id}", new UpdateUserRequest("Viewer", true)); update.EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.GetAsync("api/products")).StatusCode);
            await Login(cashier, "cashier", changedPassword);
            Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("api/products")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("api/sales", new CreateSaleRequest(Guid.NewGuid(), [new(productId, "Piece", 1)]))).StatusCode);
            using var reset = await admin.PostAsJsonAsync($"api/admin/users/{user.Id}/reset-password", new ResetPasswordRequest(AdminPassword)); reset.EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.GetAsync("api/auth/me")).StatusCode);
            using var logout = await admin.PostAsync("api/auth/logout", null); Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("api/admin/users")).StatusCode);
        }
        finally { if (api != null) { Stop(api); api.Dispose(); } Directory.Delete(workspace, true); }
    }

    private HttpClient Client() => new() { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}/"), Timeout = TimeSpan.FromSeconds(5) };
    private static async Task Login(HttpClient client, string username, string password)
    {
        client.DefaultRequestHeaders.Authorization = null;
        using var response = await client.PostAsJsonAsync("api/auth/login", new LoginRequest(username, password)); response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<LoginResult>())!.AccessToken);
    }
    private static void Provision(string workspace, JsonDocument settings)
    {
        var key = KeyVault.Create(Path.Combine(workspace, ".data", "security", "keyring.json"), Path.Combine(workspace, "recovery.key"), Master);
        try
        {
            var store = new SqlitePosStore(Path.Combine(workspace, ".data", "local.secure.db"),
                settings.RootElement.GetProperty("Installation").Deserialize<InstallationIdentity>()!, key);
            store.Initialize(); store.BootstrapAdministrator("owner", "Owner", AdminPassword);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public async Task ProductionHostRefusesUnencryptedDevelopmentAdapter()
    {
        var root = FindRoot();
        using var process = Start(root, Path.GetTempPath(), false, environment: "Production");
        await WaitForExit(process); Assert.NotEqual(0, process.ExitCode);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Agropecuaria.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("No se encuentra la raíz del proyecto.");
    }
    private Process Start(string root, string workspace, bool receiver, string? argument = null, string environment = "Development")
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var relativePath = receiver
            ? $"tools/Agropecuaria.SyncReceiver.Dev/bin/{configuration}/net10.0/Agropecuaria.SyncReceiver.Dev.dll"
            : $"src/Agropecuaria.LocalApi/bin/{configuration}/net10.0/Agropecuaria.LocalApi.dll";
        var info = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(root, relativePath));
        if (argument != null) info.ArgumentList.Add(argument);
        info.Environment["AGROPECUARIA_WORKSPACE"] = workspace;
        info.Environment["AGROPECUARIA_MASTER_PASSWORD"] = Master;
        info.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        info.Environment["Identity__Mode"] = "LocalDevelopment";
        info.Environment["Identity__AllowLegacyLocalForTests"] = "true";
        info.Environment["Sync__Enabled"] = "true";
        info.Environment["LocalApi__Port"] = apiPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["SyncReceiver__Port"] = receiverPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["Sync__Endpoint"] = $"http://127.0.0.1:{receiverPort}/sync/events";
        var process = Process.Start(info)!;
        process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine(); return process;
    }
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static void Stop(Process process)
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
    }
    private static async Task WaitForExit(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            Stop(process);
            Assert.Fail("El proceso no terminó en 15 segundos.");
        }
    }
    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            try { if (await condition()) return; }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) { }
            await Task.Delay(200);
        }
        Assert.Fail("El servicio no alcanzó el estado esperado antes del límite.");
    }
}
