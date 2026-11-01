using Agropecuaria.Client;
using Agropecuaria.Contracts;
using Xunit;

namespace Agropecuaria.Tests;

public sealed class PresentationTests
{
    [Theory]
    [InlineData("Offline", "Sin conexión con la nube")]
    [InlineData("NotConfigured", "Nube sin configurar")]
    [InlineData("AttentionRequired", "requiere atención")]
    [InlineData("Pending", "Cambios pendientes")]
    public void StatusDoesNotConfuseCloudFailureWithLocalServiceFailure(string state, string expected)
    {
        var text = ConnectionPresentation.Describe(new SyncStatus(state, 2, null, null));
        Assert.Contains(expected, text);
    }
}
