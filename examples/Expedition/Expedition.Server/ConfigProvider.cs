using Expedition.Shared;
using SharedMeta.Core;
using SharedMeta.Server.Core;

namespace Expedition.Server;

public abstract class ConfigProvider<TArg> : IMetaConfigProvider<TArg> where TArg : class
{
    private readonly string _baseUrl;

    // GetConfig is called on the RPC hot path (per-call resolve, then cached per-grain).
    // Memoize per (Major, Minor) so we never re-allocate the same branch twice.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), TArg> _branchCache = new();

    public ConfigProvider(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>Default branch reported when no client version is known. 2.x is the latest deployed.</summary>
    public MetaConfigVersion CurrentVersion => new(2, 0);

    public TArg GetConfig(MetaConfigVersion version) => _branchCache.GetOrAdd((version.Major, version.Minor), key => BuildConfig(key.Item1));

    public MetaConfigVersion ResolveLatestMatching(int major, int minor) => new(major, minor, 0);

    public string? GetDownloadUrl(MetaConfigVersion version)
        => $"{_baseUrl}/meta/{typeof(TArg).Name}/{version.Major}/{version.Minor}";

    public ReadOnlyMemory<byte> GetDownloadData(MetaConfigVersion version, IMetaSerializer serializer) =>
        serializer.Pack(GetConfig(version));

    protected abstract TArg BuildConfig(int major);
}

/// <summary>
/// Provides expedition config with two branches:
///   1.x = lean economy (rare treasures, smaller rewards)
///   2.x = boosted economy (more frequent treasures, bigger rewards)
///
/// Routing client → config branch is decided by <c>[MetaConfigVersion]</c> rules on
/// <see cref="ExpeditionConfig"/>; this provider just produces config bytes when asked
/// for a specific version. <see cref="ResolveLatestMatching"/> picks the latest patch
/// in the requested branch (here: 1.0.0 / 2.0.0 — no patch deployments yet).
/// </summary>
public class ExpeditionConfigProvider(string baseUrl) : ConfigProvider<ExpeditionConfig>(baseUrl)
{
    protected override ExpeditionConfig BuildConfig(int major)
    {
        // Branch 2.x — boosted economy, schema-2 (post-migration).
        if (major >= 2) {
            return new ExpeditionConfig {
                TreasurePercent = 25,    // ↑ from default 8 (much more loot scattered on the map)
                TreasureReward  = 75,    // ↑ from default 25 (3× per chest)
            };
        }

        // Branch 1.x — lean economy (legacy clients on the 1.2 line).
        return new ExpeditionConfig {
            TreasurePercent = 5,         // ↓ from default 8 (rare treasures)
            TreasureReward  = 10,        // ↓ from default 25 (small reward per chest)
        };
    }
}

public class PlayerConfigProvider(string baseUrl) : ConfigProvider<PlayerConfig>(baseUrl)
{
    protected override PlayerConfig BuildConfig(int major)
    {
        // Branch 2.x — boosted economy, schema-2 (post-migration).
        if (major >= 2) {
            return new PlayerConfig { MoveCost = 2 };
        }

        // Branch 1.x — lean economy (legacy clients on the 1.2 line).
        return new PlayerConfig();
    }
}