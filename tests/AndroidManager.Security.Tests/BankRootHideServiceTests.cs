using AndroidManager.Core.Models;
using AndroidManager.Security.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class BankRootHideServiceTests
{
    [Fact]
    public void HmaTemplateHint_ContainsYkbSteps()
    {
        Assert.Contains("HMA", BankRootHideService.HmaTemplateHint);
        Assert.Contains("Yapı Kredi", BankRootHideService.HmaTemplateHint);
    }

    [Fact]
    public void PackagesToHideHint_NotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(BankRootHideService.PackagesToHideHint));
    }
}
