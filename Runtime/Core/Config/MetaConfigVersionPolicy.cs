using System;
using System.Collections.Generic;
using System.Linq;

namespace SharedMeta.Core
{
    /// <summary>
    /// Declares a mapping from client app version range to a config version range on a
    /// <c>[MetaConfig]</c> class. Multiple attributes are evaluated top-to-bottom; the most
    /// specific match wins (exact literal beats wildcard).
    ///
    /// Pattern grammar:
    ///
    ///   Numeric components: "N"   = literal match, e.g. "2"
    ///                       "x"   = wildcard (captures the client value in that position)
    ///                       "N+"  = N or higher (no capture)
    ///                       "*"   = any (same as "x" in client pattern; in config = latest available)
    ///
    /// Examples:
    ///   [MetaConfigVersion(Client = "1.x.*",   Config = "1.x.*")]
    ///     → client 1.3.7 gets config branch 1.3, latest available patch
    ///
    ///   [MetaConfigVersion(Client = "1.4.3",   Config = "1.4.17+")]
    ///     → client exactly 1.4.3 gets config ≥ 1.4.17, latest available
    ///
    ///   [MetaConfigVersion(Client = "2.2+.*",  Config = "2.2.*")]
    ///     → clients 2.2.*, 2.3.*, … all get config branch 2.2, latest available patch
    ///
    /// Specificity (higher = wins): exact literal > range (N+) > capture (x/*).
    /// Ties at top priority produce a runtime error.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public class MetaConfigVersionAttribute : Attribute
    {
        /// <summary>Client version pattern, e.g. "1.x.*" or "2.2+.*".</summary>
        public string Client { get; set; } = "";

        /// <summary>
        /// Config version pattern, e.g. "1.x.*" (latest patch in client branch) or "1.4.17+"
        /// (specific minimum patch, pick latest available).
        /// </summary>
        public string Config { get; set; } = "";

        // Two equivalent calling forms:
        //   [MetaConfigVersion("1.x.*", "1.x.*")]
        //   [MetaConfigVersion(Client = "1.x.*", Config = "1.x.*")]
        public MetaConfigVersionAttribute() { }

        public MetaConfigVersionAttribute(string client, string config)
        {
            Client = client;
            Config = config;
        }
    }

    // ─── Pattern model ──────────────────────────────────────────────────────────

    /// <summary>Kind of a single component in a version pattern.</summary>
    internal enum PatternComponentKind
    {
        Literal,    // exact int match
        Capture,    // wildcard "x" or "*" — captures the client value
        RangePlus,  // "N+" — matches N or higher, no capture
        Latest,     // "*" in config position — resolve to latest available
    }

    internal readonly struct PatternComponent
    {
        public readonly PatternComponentKind Kind;
        public readonly int Value; // Literal value or RangePlus minimum

        private PatternComponent(PatternComponentKind kind, int value) { Kind = kind; Value = value; }

        public static PatternComponent Literal(int v) => new PatternComponent(PatternComponentKind.Literal, v);
        public static PatternComponent Capture()      => new PatternComponent(PatternComponentKind.Capture, 0);
        public static PatternComponent RangePlus(int min) => new PatternComponent(PatternComponentKind.RangePlus, min);
        public static PatternComponent Latest()       => new PatternComponent(PatternComponentKind.Latest, 0);

        /// <summary>Specificity score (higher = more specific). Used for tie-breaking.</summary>
        public int Specificity => Kind switch
        {
            PatternComponentKind.Literal   => 3,
            PatternComponentKind.RangePlus => 2,
            PatternComponentKind.Capture   => 1,
            PatternComponentKind.Latest    => 1,
            _                              => 0
        };

        public static PatternComponent ParseClient(string s)
        {
            if (s == "x" || s == "*") return Capture();
            if (s.EndsWith("+") && int.TryParse(s.TrimEnd('+'), out var min)) return RangePlus(min);
            if (int.TryParse(s, out var lit)) return Literal(lit);
            return Capture(); // fallback
        }

        public static PatternComponent ParseConfig(string s)
        {
            if (s == "*") return Latest();
            if (s == "x") return Capture(); // backreference — resolved at match time
            if (s.EndsWith("+") && int.TryParse(s.TrimEnd('+'), out var min)) return RangePlus(min);
            if (int.TryParse(s, out var lit)) return Literal(lit);
            return Latest();
        }
    }

    // ─── Single rule ────────────────────────────────────────────────────────────

    /// <summary>
    /// A parsed <see cref="MetaConfigVersionAttribute"/> rule. Immutable; created once at
    /// startup by <see cref="MetaConfigVersionResolver"/>.
    /// </summary>
    internal sealed class ConfigVersionRule
    {
        public readonly PatternComponent[] ClientParts;  // [major, minor, patch]
        public readonly PatternComponent[] ConfigParts;  // [major, minor, patch]
        public readonly string RawClient;
        public readonly string RawConfig;

        public int Specificity =>
            ClientParts.Sum(c => c.Specificity);

        public ConfigVersionRule(string client, string config)
        {
            RawClient = client;
            RawConfig  = config;
            ClientParts = ParseTriple(client, PatternComponent.ParseClient);
            ConfigParts = ParseTriple(config,  PatternComponent.ParseConfig);
        }

        private static PatternComponent[] ParseTriple(
            string pattern,
            Func<string, PatternComponent> parseComponent)
        {
            var parts = pattern.Split('.');
            var result = new PatternComponent[3];
            for (int i = 0; i < 3; i++)
                result[i] = i < parts.Length
                    ? parseComponent(parts[i].Trim())
                    : PatternComponent.Capture();
            return result;
        }

        /// <summary>
        /// Try to match <paramref name="clientVersion"/> against this rule's Client pattern.
        /// Returns true and populates <paramref name="captures"/> (indexed by position) on match.
        /// </summary>
        public bool TryMatch(MetaConfigVersion clientVersion, out int[] captures)
        {
            var cv = new[] { clientVersion.Major, clientVersion.Minor, clientVersion.Patch };
            captures = new int[3];

            for (int i = 0; i < 3; i++)
            {
                var comp = ClientParts[i];
                var val  = cv[i];

                switch (comp.Kind)
                {
                    case PatternComponentKind.Literal:
                        if (val != comp.Value) return false;
                        captures[i] = val;
                        break;
                    case PatternComponentKind.Capture:
                        captures[i] = val;
                        break;
                    case PatternComponentKind.RangePlus:
                        if (val < comp.Value) return false;
                        captures[i] = val;
                        break;
                }
            }
            return true;
        }

        /// <summary>
        /// Resolve the config version request given captured client values.
        /// Returns a partial version where <see cref="PatternComponentKind.Latest"/> positions
        /// are marked with -1 (caller must resolve via <see cref="IMetaConfigProvider{T}"/>).
        /// </summary>
        public (int Major, int Minor, int PatchMin, bool PatchIsLatest) ResolveConfigRequest(int[] captures)
        {
            int Get(PatternComponent comp, int capturedValue) => comp.Kind switch
            {
                PatternComponentKind.Literal   => comp.Value,
                PatternComponentKind.Capture   => capturedValue, // backref
                PatternComponentKind.RangePlus => comp.Value,    // minimum
                PatternComponentKind.Latest    => -1,            // caller resolves
                _                              => 0
            };

            var major = Get(ConfigParts[0], captures[0]);
            var minor = Get(ConfigParts[1], captures[1]);
            var patch = Get(ConfigParts[2], captures[2]);

            bool patchIsLatest = ConfigParts[2].Kind == PatternComponentKind.Latest;
            return (major, minor, patchIsLatest ? 0 : patch, patchIsLatest);
        }
    }

    // ─── Resolver ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runtime resolver that evaluates <see cref="MetaConfigVersionAttribute"/> rules declared
    /// on a config class to map a connecting client's app version to the correct config version.
    ///
    /// Usage: create one per config type at startup, pass to entity activation via
    /// <see cref="IMetaConfigProvider{TConfig}"/> (which will be extended with
    /// <c>GetVersionForClient(clientAppVersion)</c>).
    /// </summary>
    public sealed class MetaConfigVersionResolver
    {
        private readonly List<ConfigVersionRule> _rules;

        public MetaConfigVersionResolver(IEnumerable<MetaConfigVersionAttribute> attributes)
        {
            _rules = attributes
                .Select(a => new ConfigVersionRule(a.Client, a.Config))
                .OrderByDescending(r => r.Specificity) // most specific first
                .ToList();
        }

        /// <summary>
        /// Create a resolver from all <see cref="MetaConfigVersionAttribute"/>s declared on
        /// <typeparamref name="TConfig"/>. Returns null if no attributes are declared.
        /// </summary>
        public static MetaConfigVersionResolver? ForType<TConfig>()
            => ForType(typeof(TConfig));

        public static MetaConfigVersionResolver? ForType(Type configType)
        {
            var attrs = (MetaConfigVersionAttribute[])configType
                .GetCustomAttributes(typeof(MetaConfigVersionAttribute), inherit: false);
            if (attrs.Length == 0) return null;
            return new MetaConfigVersionResolver(attrs);
        }

        /// <summary>
        /// Resolve the config version request for a given client app version.
        /// Returns a <see cref="ConfigVersionRequest"/> describing which config branch and
        /// minimum patch to fetch. The caller then calls
        /// <see cref="IMetaConfigProvider{TConfig}.ResolveLatestMatching"/> to pin the patch.
        ///
        /// Returns null if no rule matches (caller should use <c>CurrentVersion</c> as fallback).
        /// </summary>
        public ConfigVersionRequest? Resolve(string? clientAppVersion)
        {
            if (string.IsNullOrEmpty(clientAppVersion)) return null;

            // Parse client app version to MetaConfigVersion (re-uses Major.Minor.Patch parsing)
            var cv = MetaConfigVersion.Parse(clientAppVersion);
            if (cv == default) return null;

            // Evaluate rules in descending specificity order
            foreach (var rule in _rules)
            {
                if (!rule.TryMatch(cv, out var captures)) continue;

                var (major, minor, patchMin, patchIsLatest) = rule.ResolveConfigRequest(captures);
                return new ConfigVersionRequest(major, minor, patchMin, patchIsLatest);
            }

            return null;
        }
    }

    /// <summary>
    /// Describes which config version branch to fetch for a specific client app version.
    /// Produced by <see cref="MetaConfigVersionResolver.Resolve"/>.
    /// </summary>
    public sealed class ConfigVersionRequest
    {
        /// <summary>Required config Major.</summary>
        public int Major { get; }
        /// <summary>Required config Minor.</summary>
        public int Minor { get; }
        /// <summary>Minimum Patch version, or 0 when <see cref="PatchIsLatest"/> is true.</summary>
        public int PatchMin { get; }
        /// <summary>
        /// True when the config pattern uses <c>*</c> for patch — caller should resolve to the
        /// latest available patch via <see cref="IMetaConfigProvider{TConfig}.ResolveLatestMatching"/>.
        /// </summary>
        public bool PatchIsLatest { get; }

        public ConfigVersionRequest(int major, int minor, int patchMin, bool patchIsLatest)
        {
            Major        = major;
            Minor        = minor;
            PatchMin     = patchMin;
            PatchIsLatest = patchIsLatest;
        }

        public override string ToString() =>
            PatchIsLatest
                ? $"{Major}.{Minor}.latest"
                : $"{Major}.{Minor}.{PatchMin}+";
    }
}
