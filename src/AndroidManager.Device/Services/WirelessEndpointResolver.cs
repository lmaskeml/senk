using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Device.Services;

/// <summary>
/// Delegates to <see cref="WirelessDiscoveryChain"/> (Guardian 3.0 timed fallback).
/// </summary>
public sealed class WirelessEndpointResolver : IWirelessEndpointResolver
{
    private readonly IWirelessDiscoveryChain _chain;

    public WirelessEndpointResolver(IWirelessDiscoveryChain chain) => _chain = chain;

    public Task<WirelessEndpoint?> ResolveAsync(
        WirelessWatchTarget target,
        CancellationToken cancellationToken = default) =>
        _chain.ResolveWiFiAsync(target, cancellationToken);
}
