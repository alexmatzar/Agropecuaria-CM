using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Infrastructure;
using Xunit;

namespace Agropecuaria.Tests;

[Trait("Category", "FirebaseEmulator")]
public sealed class FirebaseIntegrationTests
{
    [Fact]
    public async Task RealFirebaseLoginOfflineContinuityAndUserRecoveryOnFreshTerminal()
    {
        using var demo = new Demo();
        await demo.BootstrapAsync(); demo.StartAuthority();
        using var first = demo.StartTerminal(); await demo.ReadyAsync(first.Http);
        var login = await Login(first.Http, demo.Email, demo.Password);
        using var products = await first.Http.GetAsync("api/products"); products.EnsureSuccessStatusCode();
        var request = new CreateProductRequest(Guid.NewGuid(), Guid.NewGuid(), "Offline product", "Count", "Piece", 10, [new("Piece", 100)]);
        (await first.Http.PostAsJsonAsync("api/products", request)).EnsureSuccessStatusCode();
        demo.StopAuthority();
        var noLogin = await first.Http.PostAsJsonAsync("api/auth/login", new LoginRequest(demo.Email, demo.Password));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noLogin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.Http.GetAsync("api/products")).StatusCode);
        var sale = await first.Http.PostAsJsonAsync("api/sales", new CreateSaleRequest(Guid.NewGuid(), [new(request.ProductId, "Piece", 1)]));
        sale.EnsureSuccessStatusCode();
        var adminOffline = await first.Http.GetAsync("api/admin/users"); Assert.Equal(HttpStatusCode.ServiceUnavailable, adminOffline.StatusCode);
        demo.StartAuthority();
        using var fresh = await demo.EnrollFreshTerminalAsync(); await demo.ReadyAsync(fresh.Http);
        var recovered = await Login(fresh.Http, demo.Email, demo.Password);
        Assert.Equal(login.User.Id, recovered.User.Id); Assert.Equal("Admin", recovered.User.Role);
        // This identity-only fixture does not enroll commercial replication.
        Assert.Empty((await fresh.Http.GetFromJsonAsync<List<ProductView>>("api/products"))!);
        var users = await fresh.Http.GetFromJsonAsync<List<UserView>>("api/admin/users"); Assert.Single(users!);
    }

    [Fact]
    public async Task FirebaseUsersRolesPasswordsAndTenantIsolationAreEnforcedByAuthority()
    {
        using var demo = new Demo(); await demo.BootstrapAsync(); demo.StartAuthority();
        using var owner = demo.StartTerminal(); await demo.ReadyAsync(owner.Http); await Login(owner.Http, demo.Email, demo.Password);
        var cashierEmail = $"cashier-{Guid.NewGuid():N}@example.test";
        var newUser = new CreateUserRequest(cashierEmail, "Cashier", "Cashier", demo.Password, Guid.NewGuid());
        using var create = await owner.Http.PostAsJsonAsync("api/admin/users", newUser);
        create.EnsureSuccessStatusCode(); var user = (await create.Content.ReadFromJsonAsync<UserView>())!;
        using var retry = await owner.Http.PostAsJsonAsync("api/admin/users", newUser); retry.EnsureSuccessStatusCode();
        Assert.Equal(user.Id, (await retry.Content.ReadFromJsonAsync<UserView>())!.Id);
        var audit = (await owner.Http.GetFromJsonAsync<List<CloudAuditView>>("api/admin/cloud-audit"))!;
        Assert.Single(audit, e => e.Action == "UserCreated" && e.SubjectId == user.Id);
        using var cashier = demo.StartTerminal(); await demo.ReadyAsync(cashier.Http);
        var login = await Login(cashier.Http, cashierEmail, demo.Password); Assert.True(login.User.MustChangePassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.Http.GetAsync("api/products")).StatusCode);
        var newPassword = "New cashier password 456789!";
        using var change = await cashier.Http.PostAsJsonAsync("api/auth/password", new ChangePasswordRequest(demo.Password, newPassword)); change.EnsureSuccessStatusCode();
        await Login(cashier.Http, cashierEmail, newPassword);
        Assert.Equal(HttpStatusCode.OK, (await cashier.Http.GetAsync("api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.Http.GetAsync("api/admin/users")).StatusCode);
        using var update = await owner.Http.PatchAsJsonAsync($"api/admin/users/{user.Id}", new UpdateUserRequest("Viewer", true)); update.EnsureSuccessStatusCode();
        (await cashier.Http.PostAsync("api/auth/revalidate", null)).EnsureSuccessStatusCode();
        Assert.Equal("Viewer", (await cashier.Http.GetFromJsonAsync<UserView>("api/auth/me"))!.Role);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.Http.PostAsJsonAsync("api/sales", new CreateSaleRequest(Guid.NewGuid(), [new(Guid.NewGuid(), "Piece", 1)]))).StatusCode);
        using var wrongStore = demo.StartTerminal(Guid.NewGuid()); await demo.ReadyAsync(wrongStore.Http);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongStore.Http.PostAsJsonAsync("api/auth/login", new LoginRequest(demo.Email, demo.Password))).StatusCode);
        using var disabled = await owner.Http.PatchAsJsonAsync($"api/admin/users/{user.Id}", new UpdateUserRequest("Viewer", false)); disabled.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.Http.PostAsync("api/auth/revalidate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.Http.GetAsync("api/products")).StatusCode);
    }

    internal static async Task<LoginResult> Login(HttpClient client, string email, string password)
    {
        client.DefaultRequestHeaders.Authorization = null;
        using var response = await client.PostAsJsonAsync("api/auth/login", new LoginRequest(email, password)); response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResult>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken); return login;
    }
    internal sealed class Demo : IDisposable
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Collections.Concurrent.ConcurrentQueue<string>> Logs = new();
        private readonly string root;
        private readonly string configuration;
        private readonly string path = Path.Combine(Path.GetTempPath(), "agropecuaria-firebase-" + Guid.NewGuid().ToString("N"));
        private readonly InstallationIdentity identity = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "GTQ");
        private readonly int cloudPort = Port();
        private Process? cloud;
        private readonly PostgresFixture? database;
        public string Email { get; } = $"owner-{Guid.NewGuid():N}@example.test";
        public string Password => "Firebase emulator password 12345!";
        private const string Master = "Master passphrase for Firebase tests only";
        public Demo(bool usePostgres = false)
        {
            if (usePostgres) database = new PostgresFixture();
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(directory.FullName, "Agropecuaria.slnx"))) directory = directory.Parent!;
            root = directory.FullName; configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            Directory.CreateDirectory(path);
        }
        public async Task BootstrapAsync()
        {
            using var process = StartCloud("--bootstrap-owner");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { Assert.Fail("Cloud bootstrap timeout: " + string.Join("\n", Logs[process.Id])); }
            finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } }
            Assert.True(process.ExitCode == 0, string.Join("\n", Logs[process.Id]));
        }
        public void StartAuthority() => cloud = StartCloud();
        public void StopAuthority() { if (cloud != null) { Stop(cloud); cloud.Dispose(); cloud = null; } }
        private Process StartCloud(string? argument = null)
        {
            var info = Info("Agropecuaria.Cloud.Api");
            info.Environment["FIREBASE_AUTH_EMULATOR_HOST"] = "127.0.0.1:9099";
            info.Environment["Firebase__ProjectId"] = "demo-agropecuaria-cm";
            info.Environment["Cloud__UseSqliteDevelopment"] = database == null ? "true" : "false";
            if (database != null) info.Environment["ConnectionStrings__Cloud"] = database.ConnectionString;
            info.Environment["Cloud__DevelopmentDatabase"] = Path.Combine(path, "authority.db");
            info.Environment["Cloud__Port"] = cloudPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            info.Environment["Bootstrap__TenantId"] = identity.TenantId.ToString(); info.Environment["Bootstrap__StoreId"] = identity.StoreId.ToString();
            info.Environment["Bootstrap__Email"] = Email; info.Environment["AGROPECUARIA_CLOUD_OWNER_PASSWORD"] = Password;
            if (argument != null) info.ArgumentList.Add(argument);
            return Start(info);
        }
        private string PrepareTerminal(Guid? store = null)
        {
            var terminalPath = Path.Combine(path, Guid.NewGuid().ToString("N"));
            var data = Path.Combine(terminalPath, ".data"); Directory.CreateDirectory(data);
            var scope = identity with { StoreId = store ?? identity.StoreId, DeviceId = Guid.NewGuid() };
            File.WriteAllText(Path.Combine(data, "development.json"), JsonSerializer.Serialize(new
            { Installation = scope, Sync = new { Enabled = false } }));
            var key = KeyVault.Create(Path.Combine(data, "security", "keyring.json"), Path.Combine(terminalPath, "recovery.key"), Master);
            CryptographicOperations.ZeroMemory(key);
            return terminalPath;
        }
        public Terminal StartTerminal(Guid? store = null, bool commercial = false) => StartPreparedTerminal(PrepareTerminal(store), commercial);
        public async Task<Terminal> EnrollFreshTerminalAsync()
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{cloudPort}/") };
            await ReadyAsync(http);
            var terminalPath = PrepareTerminal(Guid.NewGuid());
            var settings = Path.Combine(terminalPath, ".data", "development.json");
            var info = Info("Agropecuaria.Security.Cli", tooling: true); info.ArgumentList.Add("enroll");
            info.Environment["AGROPECUARIA_WORKSPACE"] = terminalPath;
            info.Environment["Identity__AuthorityEndpoint"] = $"http://127.0.0.1:{cloudPort}/";
            info.Environment["Firebase__EmulatorEndpoint"] = "http://127.0.0.1:9099/";
            info.Environment["Firebase__ApiKey"] = "demo-only";
            info.Environment["AGROPECUARIA_ENROLL_EMAIL"] = Email; info.Environment["AGROPECUARIA_ENROLL_PASSWORD"] = Password;
            using (var enroll = Start(info))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await enroll.WaitForExitAsync(timeout.Token);
                Assert.True(enroll.ExitCode == 0, string.Join("\n", Logs[enroll.Id]));
            }
            using var document = JsonDocument.Parse(File.ReadAllText(settings));
            var actual = document.RootElement.GetProperty("Installation").Deserialize<InstallationIdentity>()!;
            Assert.Equal(identity.TenantId, actual.TenantId); Assert.Equal(identity.StoreId, actual.StoreId);
            Assert.NotEqual(identity.DeviceId, actual.DeviceId);
            Assert.DoesNotContain(Password, File.ReadAllText(settings));
            return StartPreparedTerminal(terminalPath);
        }
        private Terminal StartPreparedTerminal(string terminalPath, bool commercial = false)
        {
            var port = Port(); var info = Info("Agropecuaria.LocalApi");
            info.Environment["AGROPECUARIA_WORKSPACE"] = terminalPath; info.Environment["AGROPECUARIA_MASTER_PASSWORD"] = Master;
            info.Environment["Identity__Mode"] = "Firebase"; info.Environment["Identity__AuthorityEndpoint"] = $"http://127.0.0.1:{cloudPort}/";
            info.Environment["Firebase__EmulatorEndpoint"] = "http://127.0.0.1:9099/"; info.Environment["Firebase__ApiKey"] = "demo-only";
            info.Environment["Sync__Enabled"] = commercial ? "true" : "false";
            info.Environment["Sync__Mode"] = commercial ? "Cloud" : "Development";
            info.Environment["LocalApi__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new Terminal(Start(info), new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(15) });
        }
        private ProcessStartInfo Info(string project, bool tooling = false)
        {
            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardError = true, RedirectStandardOutput = true };
            info.ArgumentList.Add(Path.Combine(root, $"{(tooling ? "tools" : "src")}/{project}/bin/{configuration}/net10.0/{project}.dll"));
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; return info;
        }
        private static Process Start(ProcessStartInfo info)
        {
            var process = Process.Start(info)!;
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>(); Logs[process.Id] = logs;
            process.OutputDataReceived += (_, e) => { if (e.Data != null && logs.Count < 200) logs.Enqueue(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null && logs.Count < 200) logs.Enqueue(e.Data); };
            process.BeginOutputReadLine(); process.BeginErrorReadLine(); return process;
        }
        public async Task ReadyAsync(HttpClient client)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                try { if ((await client.GetAsync("health")).IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            Assert.Fail("El backend no arrancó en el tiempo esperado.");
        }
        private static int Port() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
        private static void Stop(Process process) { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } }
        public void Dispose() { StopAuthority(); database?.Dispose(); Directory.Delete(path, true); }
        public sealed class Terminal(Process process, HttpClient http) : IDisposable
        {
            public HttpClient Http => http;
            public void Dispose() { http.Dispose(); Stop(process); process.Dispose(); }
        }
    }
}
