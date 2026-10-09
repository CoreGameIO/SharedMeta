namespace SharedMeta.Core.Random
{
    /// <summary>
    /// Describes a named random declared via <see cref="NamedRandomAttribute"/> on a shared state.
    /// Emitted by the generator as a positional list on the server-side MetaProvider; the index
    /// into that list is the stable identifier used for both persistence and context access.
    /// </summary>
    public readonly struct NamedRandomDescriptor
    {
        /// <summary>Identifier from the attribute (e.g. "Combat").</summary>
        public string Name { get; }

        /// <summary>Explicit seed literal, or null to derive from entityId + ":" + Name.</summary>
        public string? SeedOverride { get; }

        public NamedRandomDescriptor(string name, string? seedOverride = null)
        {
            Name = name;
            SeedOverride = seedOverride;
        }
    }
}
