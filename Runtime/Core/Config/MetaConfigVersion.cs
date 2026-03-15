using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Core
{
    /// <summary>
    /// Version of a MetaConfig.
    /// Major = schema version (changes when code/structure changes, requires client update).
    /// Minor = data version (changes when config values change, same schema).
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public readonly partial struct MetaConfigVersion
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public int Major { get; }
        [Id(1), Key(1), MemoryPackOrder(1)] public int Minor { get; }

        [SerializationConstructor]
        public MetaConfigVersion(int major, int minor)
        {
            Major = major;
            Minor = minor;
        }

        public override string ToString() => $"{Major}.{Minor}";

        public bool Equals(MetaConfigVersion other) => Major == other.Major && Minor == other.Minor;
        public override bool Equals(object? obj) => obj is MetaConfigVersion other && Equals(other);
        public override int GetHashCode() => (Major * 397) ^ Minor;

        public static bool operator ==(MetaConfigVersion left, MetaConfigVersion right) => left.Equals(right);
        public static bool operator !=(MetaConfigVersion left, MetaConfigVersion right) => !left.Equals(right);
    }
}
