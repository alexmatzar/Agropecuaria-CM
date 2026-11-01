using System.Security.Cryptography;
using Agropecuaria.Infrastructure;
using Xunit;

namespace Agropecuaria.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requiere DPAPI de Windows."; }
}
public sealed class WindowsSecurityTests
{
    [WindowsFact]
    [Trait("Category", "WindowsOnly")]
    public void DpapiCurrentUserUnlocksAndRecoveryRebindsToWindowsProfile()
    {
        var path = Path.Combine(Path.GetTempPath(), "agro-dpapi-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        byte[]? key = null;
        try
        {
            var envelope = Path.Combine(path, "key.json"); var recovery = Path.Combine(path, "offline.key");
            key = KeyVault.Create(envelope, recovery, null, windowsDpapi: true);
            Assert.Equal(key, KeyVault.Unlock(envelope, null));
            var restored = Path.Combine(path, "restored.json"); KeyVault.Recover(envelope, recovery, restored, null, windowsDpapi: true);
            Assert.Equal(key, KeyVault.Unlock(restored, null));
            Assert.DoesNotContain(Convert.ToBase64String(key), File.ReadAllText(envelope));
        }
        finally { if (key != null) CryptographicOperations.ZeroMemory(key); Directory.Delete(path, true); }
    }
}
