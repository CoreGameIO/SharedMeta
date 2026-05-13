using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Core
{
    /// <summary>
    /// Version of a MetaConfig — three-component Major.Minor.Patch.
    ///
    /// Semantics:
    ///   Major — schema version: structural change that requires a client update and
    ///           may trigger a [MetaStateVersion] migration on entity activation.
    ///   Minor — feature version: functional change that requires a client update but
    ///           does not change the persisted state schema.
    ///   Patch — content version: config value change that can be delivered at runtime
    ///           without restarting the server or requiring a client update.
    ///
    /// Serialized as VersionTolerant so old two-field data (Major.Minor, Patch=0) is read
    /// back safely after the framework upgrade that introduced Patch.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public readonly partial struct MetaConfigVersion
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public int Major { get; }
        [Id(1), Key(1), MemoryPackOrder(1)] public int Minor { get; }
        [Id(2), Key(2), MemoryPackOrder(2)] public int Patch { get; }

        [SerializationConstructor]
        public MetaConfigVersion(int major, int minor, int patch = 0)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
        }

        /// <summary>Parse "Major.Minor" or "Major.Minor.Patch". Returns default on failure.</summary>
        public static MetaConfigVersion Parse(string? s)
        {
            if (string.IsNullOrEmpty(s)) return default;
            var parts = s.Split('.');
            if (parts.Length < 2) return default;
            if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
                return default;
            var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
            return new MetaConfigVersion(major, minor, patch);
        }

        /// <summary>
        /// Canonical 3-component representation <c>"Major.Minor.Patch"</c>. Always includes
        /// the patch component even when it's <c>0</c> — keeps logs / diagnostics unambiguous
        /// (a bare <c>"0.1"</c> in an error message is indistinguishable from a 2-component
        /// short-form, but the framework's version space is always 3 components).
        /// <see cref="Parse"/> still accepts both <c>"M.m"</c> and <c>"M.m.p"</c> input for
        /// backward compatibility with user-typed config strings.
        /// </summary>
        public override string ToString() => $"{Major}.{Minor}.{Patch}";

        public bool Equals(MetaConfigVersion other) =>
            Major == other.Major && Minor == other.Minor && Patch == other.Patch;
        public override bool Equals(object? obj) => obj is MetaConfigVersion other && Equals(other);
        public override int GetHashCode() => ((Major * 397) ^ Minor) * 397 ^ Patch;

        public static bool operator ==(MetaConfigVersion left, MetaConfigVersion right) => left.Equals(right);
        public static bool operator !=(MetaConfigVersion left, MetaConfigVersion right) => !left.Equals(right);

        /// <summary>Lexicographic ordering: Major, then Minor, then Patch.</summary>
        public static bool operator <(MetaConfigVersion l, MetaConfigVersion r) =>
            l.Major < r.Major || (l.Major == r.Major && (l.Minor < r.Minor || (l.Minor == r.Minor && l.Patch < r.Patch)));
        public static bool operator >(MetaConfigVersion l, MetaConfigVersion r) => r < l;
        public static bool operator <=(MetaConfigVersion l, MetaConfigVersion r) => !(l > r);
        public static bool operator >=(MetaConfigVersion l, MetaConfigVersion r) => !(l < r);
    }
}
