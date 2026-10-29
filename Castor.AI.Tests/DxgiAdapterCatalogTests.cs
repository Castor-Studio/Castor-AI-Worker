using Castor.AI.Providers.Local;
using Xunit.Abstractions;

namespace Castor.AI.Tests;

public sealed class DxgiAdapterCatalogTests(ITestOutputHelper output)
{
    [Fact]
    public void Enumerates_named_adapters_in_dxgi_order()
    {
        if (!OperatingSystem.IsWindows()) return;

        var adapters = DxgiAdapterCatalog.Enumerate();
        Assert.NotEmpty(adapters);
        for (var index = 0; index < adapters.Count; index++)
        {
            Assert.Equal(index, adapters[index].Index);
            Assert.False(string.IsNullOrWhiteSpace(adapters[index].Name));
            output.WriteLine($"{adapters[index].Index}: {adapters[index].Name}");
        }
    }
}
