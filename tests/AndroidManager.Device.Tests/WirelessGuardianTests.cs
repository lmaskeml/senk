using AndroidManager.Core.Models;
using AndroidManager.Device.Services;
using Xunit;

namespace AndroidManager.Device.Tests;

public sealed class DeviceIdentityServiceTests
{
    private readonly DeviceIdentityService _svc = new();

    [Fact]
    public void ComputeStableId_PrefersDeviceId()
    {
        var id = _svc.ComputeStableId("abc123", "SERIAL1", "Xiaomi", "11T Pro");
        Assert.Equal("cid:abc123", id);
    }

    [Fact]
    public void ComputeStableId_FallsBackToSerial()
    {
        var id = _svc.ComputeStableId(null, "SERIAL1", "Xiaomi", "11T Pro");
        Assert.Equal("serial:SERIAL1", id);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(6, 30)]
    [InlineData(7, 60)]
    public void Backoff_IncreasesThenCaps(int attempt, int expectedSeconds)
    {
        var delay = WirelessGuardianBackoff.DelayForAttempt(attempt);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }
}

public sealed class WirelessEndpointResolverIdentityTests
{
    [Fact]
    public void EndpointDiscoverySource_HasExpectedPriorityValues()
    {
        Assert.True((int)EndpointDiscoverySource.Mdns < (int)EndpointDiscoverySource.Companion);
        Assert.True((int)EndpointDiscoverySource.Companion < (int)EndpointDiscoverySource.Cached);
    }
}
