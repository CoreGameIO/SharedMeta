using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClanWars.Shared;
using ClanWars.Shared.Client;
using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Core.Transport;
using SharedMeta.Debug.Mux;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Transport.SignalR;

namespace ClanWars.Client.Common
{
    /// <summary>
    /// One simulated player: owns an <see cref="IConnection"/> (plain SignalR OR a logical
    /// channel on a shared Mux socket), a MetaClient, and a loop of random clan actions.
    /// Records per-action latency + success/error into the shared
    /// <see cref="MetricsCollector"/>. Stops when the cancellation token fires (driven by the
    /// runner's <see cref="StressTestOptions.DurationSeconds"/>).
    /// </summary>
    public class PlayerSimulator
    {
        private readonly StressTestOptions _options;
        private readonly MetricsCollector _metrics;
        private readonly SharedClanRegistry _sharedClans;
        private readonly string _playerId;
        private readonly Random _rng;
        private readonly MuxChannel? _muxChannel;
        private readonly int? _muxTag;

        public PlayerSimulator(
            StressTestOptions options,
            MetricsCollector metrics,
            SharedClanRegistry sharedClans,
            string playerId,
            int rngSeed,
            MuxChannel? muxChannel = null,
            int? muxTag = null)
        {
            _options = options;
            _metrics = metrics;
            _sharedClans = sharedClans;
            _playerId = playerId;
            _rng = new Random(rngSeed);
            _muxChannel = muxChannel;
            _muxTag = muxTag;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            // Connect & resolve API. When _muxChannel is set this simulator rides a shared
            // SignalR socket (debug Mux transport) instead of opening its own — the rest of
            // the MetaClient stack is transport-agnostic so nothing else changes.
            IConnection connection = _muxChannel != null
                ? _muxChannel.CreateConnection(_muxTag)
                : new SignalRConnection(_options.ServerUrl, clientVersion: _options.ClientAppVersion);
            var client = new MetaClient(
                connection,
                new MemoryPackMetaSerializer(),
                new MetaClientOptions
                {
                    PlayerId = _playerId,
                    ClientAppVersion = _options.ClientAppVersion,
                });
            // Register a static ClanConfig that matches the simulator's app version. v1 clients
            // never touch MaxFriendships / MaxOfficers in their method paths so the defaults
            // are harmless; v2 clients use the full shape. The server resolves its own branch
            // independently — this provider just satisfies the client-side API contract that
            // every [MetaService] backed by a [MetaConfig] has a provider before subscription.
            var resolver = (MetaServiceResolver)client.Resolver;
            resolver.RegisterConfigProvider<ClanConfig>(new StaticConfigProvider<ClanConfig>(BuildClientConfig(_options.ClientAppVersion)));
            resolver.RegisterAllServices();

            try
            {
                await TimedAsync("Connect", async () => { await client.ConnectAsync(); return true; });
                var profileApi = await TimedAsync("ResolveProfile",
                    async () => await client.Resolver.GetServiceAsync<ProfileServiceApiClient>(_playerId));
                if (profileApi == null) return;

                // Query-mode RPC client for GetRecommendedClans (no subscription, no replay).
                // The regular ApiClient's local wrapper would return an empty list (server-only
                // DI is unreachable client-side); this one hits the silo and returns real data.
                var profileQuery = new ProfileServiceQueryApi(connection, new MemoryPackMetaSerializer()).EntityApi(_playerId);
                // Query-mode client for the clan preview path. EntityApi(clanId) is created
                // per-candidate inside the step loop (cheap value-/struct-like binding). Using
                // Query instead of GetServiceAsync<ClanServiceApiClient> means the player does
                // NOT subscribe to every candidate they look at — subscriptions are reserved
                // for actual members, enforced server-side by IClanService.AccessPolicy = Authorized.
                var clanQuery = new ClanServiceQueryApi(connection, new MemoryPackMetaSerializer());

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await StepAsync(client, profileApi, profileQuery, clanQuery, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _metrics.Record("step-exception", 0, ok: false);
                        Console.Error.WriteLine($"[{_playerId}] step exception: {ex.Message}");
                    }
                    var delayMs = _rng.Next(0, _options.MeanDelayMs * 2 + 1);
                    try { await Task.Delay(delayMs, ct); } catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                try { await client.DisposeAsync(); }
                catch { /* swallow on shutdown */ }
            }
        }

        private async Task StepAsync(MetaClient client, ProfileServiceApiClient profileApi, ProfileServiceEntityQueryApi profileQuery, ClanServiceQueryApi clanQuery, CancellationToken ct)
        {
            // Weighted action picker. GainPoints dominates so we get a steady stream of clan-
            // power broadcasts; clan management actions are rarer but produce the version-
            // sensitive cross-entity / broadcast paths we want to exercise.
            var roll = _rng.NextDouble();
            if (roll < 0.60)
            {
                await TimedVoid("GainPoints", () => profileApi.GainPointsAsync(_rng.Next(1, 20)));
                return;
            }

            // Cached summary so we know whether we're in a clan + what invitations are parked.
            var summary = profileApi.GetSummary();

            // Approved-invitation decision branch — independent of in-clan / not-in-clan.
            // Free player: accept first parked invitation (single-shot join). In-clan player:
            // 20% chance to switch to a parked invitation, 80% stay. Both paths converge on
            // AcceptInvitation which handles LeaveClan + ConfirmJoin internally.
            if (summary.ApprovedInvitations is { Count: > 0 } invs)
            {
                var freePlayer = string.IsNullOrEmpty(summary.ClanId);
                if (freePlayer || _rng.NextDouble() < 0.20)
                {
                    var target = invs[_rng.Next(invs.Count)];
                    await TimedAsync("AcceptInvitation",
                        async () => await profileApi.AcceptInvitationAsync(target));
                    return;
                }
            }

            if (string.IsNullOrEmpty(summary.ClanId))
            {
                // Not in a clan. Periodically fetch server-side recommendations so this
                // process sees clans created by the OTHER process (the mechanism that
                // produces cross-version subscriptions → force-patch on the server).
                // Recommendations are the union of v1- and v2-pinned clans (the server
                // doesn't filter by config version), so a v1 player will naturally land
                // on a v2-pinned clan ~half the time when both processes run together.
                if (_sharedClans.ShouldRefresh())
                {
                    var rec = await TimedAsync("GetRecommendedClans",
                        async () => await profileQuery.GetRecommendedClansAsync(20));
                    if (rec != null) _sharedClans.MergeRecommendations(rec);
                }

                // Try create or apply. Cap is reservation-based via TryReserveCreate — atomic
                // Interlocked.Increment closes the read-then-check race that previously let
                // hundreds of concurrent players blow past MaxClans (148 created with cap=20
                // on a 500-player run was the symptom). On CreateClan failure we explicitly
                // release the slot back to the pool.
                if (roll < 0.75 && _sharedClans.TryReserveCreate(_options.MaxClans))
                {
                    var name = $"clan-{_playerId}-{_rng.Next(1000)}";
                    var clanId = await TimedAsync("CreateClan",
                        async () => await profileApi.CreateClanAsync(name));
                    if (!string.IsNullOrEmpty(clanId))
                        _sharedClans.AddOwn(clanId);
                    else
                        _sharedClans.ReleaseReservation();
                }
                else
                {
                    var candidate = _sharedClans.PickRandom(_rng);
                    if (candidate != null)
                    {
                        // Preview via Query — no subscription. Earlier draft acquired a full
                        // ClanServiceApiClient (which subscribes the player to the clan entity)
                        // just to peek at the summary; that turned every "window shopper" into a
                        // permanent broadcast recipient and inflated avg fan-out per clan to 200+.
                        // Now we hit the OpenAccess-flagged GetSummary Query directly — single
                        // RPC, zero subscriber-list mutation. Subscriptions are reserved for
                        // actual members (gated server-side by IClanService.IsAuthorized).
                        //
                        // Cross-version subscriptions for the force-patch demo are still
                        // manufactured later: when AcceptApplication runs and the v1 player
                        // becomes a member of a v2-pinned clan, the resulting subscribe path
                        // triggers the ConfigBoundary check.
                        await TimedAsync("PreviewClan",
                            async () => await clanQuery.EntityApi(candidate).GetSummaryAsync());
                        await TimedVoid("ApplyToClan", () => profileApi.ApplyToClanAsync(candidate));
                    }
                }
            }
            else
            {
                // In a clan — sometimes leave, sometimes interact via clan API (force-patch
                // demonstrates here for v1 clients hitting a v2-pinned clan).
                if (roll > 0.95)
                {
                    await TimedAsync("LeaveClan", async () => await profileApi.LeaveClanAsync());
                }
                else
                {
                    // Acquire ClanServiceApiClient lazily — server may downgrade to ServerPatch
                    // for v1 client × v2-pinned clan. The metric tags the call regardless of mode;
                    // the wire-level latency naturally reflects the mode picked by the server.
                    var clanApi = await TimedAsync("ResolveClan",
                        async () => await client.Resolver.GetServiceAsync<ClanServiceApiClient>(summary.ClanId!));
                    if (clanApi == null) return;

                    // Leader review: if this player is the leader of THIS clan, periodically
                    // process pending applications. Read the local cached ClanState (the subscribe
                    // populated it on resolve) and decide 50/50 accept/reject for each. Limit per
                    // step to a small batch so we don't monopolize a single step on busy clans.
                    var clanState = client.Resolver.GetState<ClanState>(summary.ClanId!);
                    if (clanState != null && clanState.LeaderId == _playerId
                        && clanState.Applications.Count > 0)
                    {
                        const int MaxReviewsPerStep = 4;
                        var apps = clanState.Applications;
                        var reviewCount = System.Math.Min(MaxReviewsPerStep, apps.Count);
                        for (int i = 0; i < reviewCount; i++)
                        {
                            // Snapshot pick — apps list may shift after each call as the
                            // accepted/rejected entry is removed server-side and the local cache
                            // catches up via broadcast. Re-pick from the snapshot index 0.
                            var applicantId = apps.Count > 0 ? apps[0] : null;
                            if (applicantId == null) break;
                            if (_rng.NextDouble() < 0.50)
                                await TimedAsync("AcceptApplication",
                                    async () => await clanApi.AcceptApplicationAsync(applicantId));
                            else
                                await TimedAsync("RejectApplication",
                                    async () => await clanApi.RejectApplicationAsync(applicantId));
                        }
                    }

                    // Token query for the clan — exercises cap layer + (potentially) patch path.
                    // GetSummary is a sync Query method that reads cached state via the active
                    // MetaContext (AsyncLocal). Calling it inside Task.Run would break AsyncLocal
                    // flow and the context lookup would fail intermittently — invoke inline.
                    await TimedVoid("ClanGetSummary", () => { _ = clanApi.GetSummary(); return Task.CompletedTask; });
                }
            }
        }

        private static ClanConfig BuildClientConfig(string appVersion)
        {
            // Pick the config shape the client believes it's running. The server still resolves
            // its branch from MetaConfigVersion attributes — this is purely the client's view.
            if (appVersion.StartsWith("2."))
                return new ClanConfig { CreateClanCost = 100, MaxMembers = 100, MaxFriendships = 10, MaxOfficers = 5 };
            return new ClanConfig { CreateClanCost = 100, MaxMembers = 100 };
        }

        private async Task<T?> TimedAsync<T>(string action, Func<Task<T>> work)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await work();
                sw.Stop();
                _metrics.Record(action, sw.Elapsed.TotalMilliseconds, ok: true);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _metrics.Record(action, sw.Elapsed.TotalMilliseconds, ok: false);
                if (action != "step-exception") Console.Error.WriteLine($"[{_playerId}] {action} failed: {ex.Message}");
                return default;
            }
        }

        private async Task TimedVoid(string action, Func<Task> work)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await work();
                sw.Stop();
                _metrics.Record(action, sw.Elapsed.TotalMilliseconds, ok: true);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _metrics.Record(action, sw.Elapsed.TotalMilliseconds, ok: false);
                Console.Error.WriteLine($"[{_playerId}] {action} failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Thread-safe registry of clans known to players within the current client process.
    /// Two-source population: (1) local "I just created clan X" notifications via <see cref="Add"/>,
    /// (2) periodic server-driven <c>GetRecommendedClans</c> queries that merge in clans created
    /// by the OTHER client process — required to manufacture cross-version subscriptions
    /// (v1-process player applying to a v2-pinned clan and vice versa) for the force-patch demo.
    /// </summary>
    public class SharedClanRegistry
    {
        private readonly List<string> _ids = new();
        private readonly object _lock = new();
        // Reservation counter. Incremented by TryReserveCreate BEFORE the CreateClan RPC fires,
        // so concurrent callers see each other's pending creates and stop early at the cap.
        // Decremented by ReleaseReservation on RPC failure. AddOwn does NOT increment — the
        // reservation already counted the slot at the gate.
        private int _ownCount;
        private DateTime _nextRefreshAt = DateTime.MinValue;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

        /// <summary>Total clan IDs known to this process (own + recommendations).</summary>
        public int KnownCount { get { lock (_lock) return _ids.Count; } }

        /// <summary>Count of clan-creation slots claimed by this process (successful + in-flight
        /// reservations). Used by the soft-cap gate as the source of truth. Atomic increment in
        /// <see cref="TryReserveCreate"/> closes the previous read-then-check race that let
        /// hundreds of concurrent callers blow past <c>MaxClans</c>.</summary>
        public int OwnCreatedCount => Interlocked.CompareExchange(ref _ownCount, 0, 0);

        /// <summary>Atomically reserve a slot for one CreateClan attempt. Returns true if the
        /// post-increment count is at or under <paramref name="maxClans"/>; otherwise rolls the
        /// counter back and returns false. Caller must invoke either <see cref="AddOwn"/> on
        /// success (no further increment) or <see cref="ReleaseReservation"/> on failure.</summary>
        public bool TryReserveCreate(int maxClans)
        {
            if (Interlocked.Increment(ref _ownCount) <= maxClans) return true;
            Interlocked.Decrement(ref _ownCount);
            return false;
        }

        /// <summary>Release a slot reserved by <see cref="TryReserveCreate"/> when the
        /// downstream CreateClan RPC fails or returns null.</summary>
        public void ReleaseReservation() => Interlocked.Decrement(ref _ownCount);

        /// <summary>Register a clan THIS process just created — pinned at our version.
        /// Does NOT increment the reservation counter; the caller must have already reserved
        /// via <see cref="TryReserveCreate"/>.</summary>
        public void AddOwn(string clanId)
        {
            lock (_lock)
            {
                if (!_ids.Contains(clanId)) _ids.Add(clanId);
            }
        }

        public string? PickRandom(Random rng)
        {
            lock (_lock) return _ids.Count == 0 ? null : _ids[rng.Next(_ids.Count)];
        }

        public bool ShouldRefresh()
        {
            lock (_lock)
            {
                if (DateTime.UtcNow < _nextRefreshAt) return false;
                _nextRefreshAt = DateTime.UtcNow + RefreshInterval;
                return true;
            }
        }

        public void MergeRecommendations(System.Collections.Generic.IEnumerable<ClanWars.Shared.ClanSummary> recs)
        {
            lock (_lock)
            {
                foreach (var r in recs)
                    if (!_ids.Contains(r.ClanId)) _ids.Add(r.ClanId);
            }
        }
    }
}
