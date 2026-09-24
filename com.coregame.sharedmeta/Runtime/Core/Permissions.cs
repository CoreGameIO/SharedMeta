using System;
using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Core
{
    /// <summary>
    /// The permissions a player holds, as the server reported them for this session.
    /// Entitlements of the account — <c>Cheat</c>, <c>Admin</c>, <c>Support</c> — not in-game
    /// roles, which belong in entity state.
    /// </summary>
    /// <remarks>
    /// Names travel, not bit positions. A bit index is only meaningful against the table that
    /// produced it, and a client build's table can differ from the silo's — the same version skew
    /// the signature handshake exists to absorb. Comparing names costs a few string equality checks
    /// on a handful of gated methods and cannot be wrong across builds.
    /// <para>
    /// <see cref="Generation"/> lets a client drop an update that arrives out of order: the set is
    /// resolved once per session and replaced by a push when entitlements change, and two pushes
    /// racing on different transports must not leave the older one in place.
    /// </para>
    /// </remarks>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class PlayerPermissions
    {
        /// <summary>Permissions held. Empty means the player holds none, which is not the same as unknown.</summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public string[] Names { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Bumped by the server every time a player's entitlements change. Monotonic per player;
        /// a client keeps the highest it has seen.
        /// </summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public int Generation { get; set; }

        /// <summary>True when this set contains <paramref name="permission"/> (ordinal, case-sensitive).</summary>
        public bool Has(string permission)
        {
            var names = Names;
            if (names == null) return false;
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], permission, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Consulted at the top of generated <c>*ApiClient</c> methods carrying
    /// <c>[RequirePermission]</c>, so a call the server would refuse fails locally instead of
    /// travelling. Mirrors the shape of <c>CapabilitiesGate</c> for rejected methods.
    /// </summary>
    /// <remarks>
    /// This gate is a convenience, never the enforcement point: it fails open on an unknown set
    /// (<c>null</c> — no session payload yet, or a server that does not report permissions), where
    /// the server's own gate fails closed. Blocking locally on "unknown" would break every client
    /// against a host that never wired entitlements, while letting an unknown set through costs at
    /// most one refused round trip.
    /// </remarks>
    public static class PermissionGate
    {
        /// <summary>
        /// True when the call is admissible: the set is unknown (pass-through), no permission is
        /// required, or at least one required permission is held.
        /// </summary>
        public static bool IsAllowed(PlayerPermissions? held, string[] required)
        {
            if (required == null || required.Length == 0) return true;
            if (held == null) return true;                 // unknown set — let the server decide
            return Holds(held, required);
        }

        /// <summary>
        /// Strict evaluation for the enforcement point: an unknown set holds nothing. A player whose
        /// entitlements could not be read is refused rather than admitted.
        /// </summary>
        public static bool Holds(PlayerPermissions? held, string[] required)
            => Holds(held?.Names, required);

        /// <summary>
        /// Strict evaluation against the raw names carried on a call
        /// (<c>RpcCall.CallerPermissions</c>). Null or empty holds nothing.
        /// </summary>
        public static bool Holds(string[]? held, string[] required)
        {
            if (required == null || required.Length == 0) return true;
            if (held == null || held.Length == 0) return false;

            for (int i = 0; i < required.Length; i++)
            {
                for (int j = 0; j < held.Length; j++)
                {
                    if (string.Equals(held[j], required[i], StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The server-side enforcement point, called by the generated provider override for a
        /// client-originated call. Synchronous by design: the caller's set rides on the call, so a
        /// gated method costs a comparison rather than a read of the entitlements store.
        /// </summary>
        public static void EnsureHeld(string[]? held, string[] required, string service, string method)
        {
            if (!Holds(held, required))
                throw new MetaPermissionDeniedException(service, method, required);
        }

        /// <summary>
        /// Builds the exception thrown at the gate. Keeps the generated emit a single line.
        /// </summary>
        public static MetaPermissionDeniedException Denied(string service, string method, string[] required)
            => new MetaPermissionDeniedException(service, method, required);
    }

    /// <summary>
    /// Thrown when a caller lacks every permission a method requires — on the client at the gate
    /// before the call is sent, and on the server by the generated dispatcher for a caller that
    /// bypassed it.
    /// </summary>
    /// <remarks>
    /// <see cref="GenerateSerializerAttribute"/> so the type survives the EntityGrain →
    /// SessionManagerGrain hop; without it Orleans wraps the throw in a codec error and a
    /// <c>catch (MetaPermissionDeniedException)</c> upstream never matches.
    /// </remarks>
    [GenerateSerializer]
    public class MetaPermissionDeniedException : Exception
    {
        /// <summary>Service interface name the refused method belongs to.</summary>
        [Id(0)] public string Service { get; private set; }

        /// <summary>Method alias that was refused.</summary>
        [Id(1)] public string Method { get; private set; }

        /// <summary>Permissions that would have admitted the call — any one of them.</summary>
        [Id(2)] public string[] Required { get; private set; }

        public MetaPermissionDeniedException(string service, string method, string[] required)
            : base(BuildMessage(service, method, required))
        {
            Service = service ?? "";
            Method = method ?? "";
            Required = required ?? Array.Empty<string>();
        }

        /// <summary>
        /// Parameterless constructor for the Orleans serializer, which materializes the instance
        /// and then fills the <c>[Id]</c> members.
        /// </summary>
        protected MetaPermissionDeniedException() : base()
        {
            Service = "";
            Method = "";
            Required = Array.Empty<string>();
        }

        private static string BuildMessage(string service, string method, string[] required)
        {
            var names = required == null || required.Length == 0 ? "<none>" : string.Join(" or ", required);
            return $"Caller lacks permission for '{service}.{method}' — requires {names}.";
        }
    }
}
