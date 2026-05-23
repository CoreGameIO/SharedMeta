using System.Collections.Generic;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace ClanWars.Shared
{
    /// <summary>
    /// Profile service implementation. Cross-entity calls into <see cref="IClanService"/>
    /// for clan operations; the server-only <see cref="IClanContainerService"/> is reached
    /// via DI on the silo side (Context.Resolver) and is NOT visible to clients.
    /// </summary>
    [MetaServiceImpl(typeof(IProfileService), typeof(ProfileState), typeof(IClanService))]
    public partial class ProfileService : IProfileService
    {
        private ProfileState S => State;
        private ClanConfig C => (ClanConfig)Config!;

        public Task GainPoints(int amount)
        {
            if (amount <= 0) return Task.CompletedTask;
            S.Score += amount;

            // Forward the delta to the clan (cross-entity, fire-and-forget). Profile doesn't
            // need to wait for the clan grain to apply the delta — clan broadcasts its own
            // State.Power change to its subscribers independently, and profile never reads
            // clan state after this call. Saves one grain-to-grain await on the hot path.
            if (!string.IsNullOrEmpty(S.ClanId))
            {
                GetIClanService(S.ClanId).AddPower(amount);
            }
            return Task.CompletedTask;
        }

        public async Task<string?> CreateClan(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (S.Money < C.CreateClanCost) return null;
            if (!string.IsNullOrEmpty(S.ClanId)) return null;  // already in a clan

            // Derive a deterministic clan id from the player + name so a retry of the same
            // creation produces the same entity (stress-test friendliness).
            var clanId = $"clan-{Context.CallerId ?? "anon"}-{name}";

            S.Money -= C.CreateClanCost;
            S.ClanId = clanId;

            var clan = GetIClanService(clanId);
            var ok = await clan.Initialize(Context.CallerId ?? "anon", name);
            if (!ok)
            {
                // Rollback if the clan grain rejected the initialize (e.g. concurrent create).
                S.Money += C.CreateClanCost;
                S.ClanId = null;
                return null;
            }
            // Seed initial clan power with the player's current score (OneWay — see GainPoints
            // commentary; same reasoning applies here).
            if (S.Score > 0)
                clan.AddPower(S.Score);

            return clanId;
        }

        public async Task<ApplyResult> ApplyToClan(string clanId)
        {
            if (string.IsNullOrEmpty(clanId)) return ApplyResult.NoClan;
            if (!string.IsNullOrEmpty(S.ClanId)) return ApplyResult.AlreadyIn;  // already in one
            if (S.PendingApplications.Contains(clanId)) return ApplyResult.Contains;

            S.PendingApplications.Add(clanId);
            var clan = GetIClanService(clanId);
            await clan.SubmitApplication(Context.CallerId ?? "anon");
            return ApplyResult.Ok;
        }

        public async Task<bool> LeaveClan()
        {
            var clanId = S.ClanId;
            if (string.IsNullOrEmpty(clanId)) return false;

            var clan = GetIClanService(clanId);
            var removed = await clan.RemoveMember(Context.CallerId ?? "anon", S.Score);
            if (!removed) return false;

            S.ClanId = null;
            return true;
        }

        public List<ClanSummary> GetRecommendedClans(int limit)
        {
            // Query mode body. The local sync wrapper on ApiClient returns empty client-side
            // (server-only DI is unreachable); meaningful callers MUST use the generated
            // ProfileServiceQueryApi RPC variant to hit the server's container snapshot.
            if (!Context.IsServer) return new List<ClanSummary>();
            return Context.ResolveService<IClanContainerService>().GetRecommended(limit);
        }

        public ProfileSummary GetSummary()
            => new()
            {
                Score = S.Score,
                Money = S.Money,
                ClanId = S.ClanId,
                ApprovedInvitations = S.ApprovedInvitations.Count == 0
                    ? null
                    : new List<string>(S.ApprovedInvitations),
                HeroCount = S.Heroes.Count,
                InventoryCount = S.Inventory.Count,
                LevelsCompleted = S.CompletedLevels.Count,
            };

        /// <summary>
        /// Receive a membership offer from a clan that accepted our application. If we're free,
        /// commit immediately (set ClanId + send ConfirmJoin to clan). If we're already in
        /// another clan, park the offer for a later switch decision.
        /// </summary>
        public async Task OfferMembership(string clanId)
        {
            if (string.IsNullOrEmpty(clanId)) return;
            // Always drop the matching pending app — the leader's decision is now reflected.
            S.PendingApplications.Remove(clanId);

            // IMPORTANT: this method is called CROSS-ENTITY from clan.AcceptApplication, so
            // Context.CallerId here is NOT the player — it's the previous-hop grain (clan) /
            // original client caller (the leader of that clan). We need our OWN playerId, which
            // for a Private-scoped Profile entity equals Context.EntityId.
            var playerId = Context.EntityId ?? "anon";

            if (string.IsNullOrEmpty(S.ClanId))
            {
                // Free — join immediately. Await ConfirmJoin so we only commit S.ClanId on
                // confirmed-in-roster: a concurrent join via another path may have filled the
                // clan since AcceptApplication left a slot open, in which case ConfirmJoin
                // returns false and we leave S.ClanId null. This also closes the race window
                // that previously caused ResolveClan failures (subscribe before IsAuthorized
                // saw the new Member).
                var joined = await GetIClanService(clanId).ConfirmJoin(playerId, S.Score);
                if (joined)
                {
                    S.ClanId = clanId;
                    S.PendingApplications.Clear();
                    S.ApprovedInvitations.Remove(clanId);
                }
            }
            else if (S.ClanId != clanId && !S.ApprovedInvitations.Contains(clanId))
            {
                // Busy — park as approved invitation for later AcceptInvitation.
                S.ApprovedInvitations.Add(clanId);
            }
        }

        /// <summary>
        /// Switch from current clan (if any) to a previously-approved invitation. Returns false
        /// when the target invitation is unknown or the leave step rejects.
        /// </summary>
        public async Task<bool> AcceptInvitation(string clanId)
        {
            if (string.IsNullOrEmpty(clanId)) return false;
            if (!S.ApprovedInvitations.Contains(clanId)) return false;

            // Use EntityId rather than CallerId — same reasoning as in OfferMembership. Even
            // though AcceptInvitation is client-initiated (where CallerId == EntityId for a
            // UserOwned entity), keep the canonical reference so a future caller-chain change
            // can't silently misroute the playerId argument.
            var playerId = Context.EntityId ?? "anon";

            if (!string.IsNullOrEmpty(S.ClanId) && S.ClanId != clanId)
            {
                var current = GetIClanService(S.ClanId);
                var removed = await current.RemoveMember(playerId, S.Score);
                if (!removed) return false;
                S.ClanId = null;
            }

            // Await ConfirmJoin so the new clan has us in Members before we declare S.ClanId.
            // Subsequent ResolveClan would otherwise race the fire-and-forget and fail
            // IsAuthorized. If ConfirmJoin returns false (roster full), drop the invitation
            // and leave the player clan-less.
            var joined = await GetIClanService(clanId).ConfirmJoin(playerId, S.Score);
            S.ApprovedInvitations.Remove(clanId);
            if (!joined) return false;
            S.ClanId = clanId;
            S.PendingApplications.Clear();
            return true;
        }

        public Task<bool> DeclineInvitation(string clanId)
        {
            return Task.FromResult(S.ApprovedInvitations.Remove(clanId));
        }

        // ── Day-to-day profile mutations (mobile-RPG meta loop) ───────────────────

        // Item & hero catalog templates — kept inline so the stress test is self-contained.
        private static readonly string[] HeroClasses = { "Warrior", "Mage", "Rogue", "Cleric", "Archer", "Paladin", "Necromancer", "Ranger" };
        private static readonly string[] HeroNames = {
            "Aelinor","Brakkon","Cynthia","Drakar","Elwyn","Fenric","Gildara","Halric","Iyara","Jorvik",
            "Kaelen","Lirien","Morwen","Nyx","Orin","Pelias","Quintus","Rhea","Soren","Talia",
        };
        private static readonly string[] ItemSlots = { "head","chest","legs","weapon","offhand","trinket" };
        private static readonly string[] AffixStats = { "Attack","Health","Defense","Speed","CritChance","CritDamage","Resistance","Accuracy" };
        private static readonly string[] ItemPrefixes = {
            "Ancient","Eternal","Cursed","Blessed","Frozen","Burning","Shadowed","Radiant",
            "Stormforged","Bloodbound","Spiritual","Demonic","Crystalline","Verdant","Obsidian","Adamantine",
        };
        private static readonly string[] ItemSuffixes = {
            "of the Fallen","of Endless Sorrow","of the Tempest","of Ancient Wisdom",
            "of Burning Vengeance","of the Forgotten","of Twilight","of the Heroes",
            "of the Northern Realms","of the Sundered Crown","of the First Dawn","of Whispered Pacts",
        };

        public Task<bool> BuyItem(int tier)
        {
            if (tier < 1) tier = 1;
            if (tier > 6) tier = 6;
            var cost = 20 * tier * tier;
            if (S.Money < cost) return Task.FromResult(false);
            S.Money -= cost;
            S.Inventory.Add(GenerateItem(tier));
            return Task.FromResult(true);
        }

        public Task<bool> SellItem(int inventoryIndex)
        {
            if (inventoryIndex < 0 || inventoryIndex >= S.Inventory.Count) return Task.FromResult(false);
            var item = S.Inventory[inventoryIndex];
            S.Money += 10 * item.Tier * item.Tier;
            S.Inventory.RemoveAt(inventoryIndex);
            return Task.FromResult(true);
        }

        public Task<bool> HireHero(string heroClass)
        {
            const int HireCost = 500;
            if (S.Money < HireCost) return Task.FromResult(false);
            var cls = string.IsNullOrEmpty(heroClass) ? HeroClasses[0] : heroClass;
            S.Money -= HireCost;
            var heroId = S.NextEntityId++;
            S.Heroes.Add(new HeroData
            {
                HeroId = heroId,
                Name = $"{HeroNames[heroId % HeroNames.Length]}-{heroId}",
                Class = cls,
                Level = 1,
                Stars = 1,
                Power = 100,
                Stats = new HeroStats
                {
                    Health = 1000, Attack = 100, Defense = 50, Speed = 20,
                    CritChance = 5, CritDamage = 150, Resistance = 20, Accuracy = 100,
                },
                Skills = new List<string> { $"{cls}-basic", $"{cls}-passive" },
            });
            return Task.FromResult(true);
        }

        public Task<bool> LevelUpHero(int heroId)
        {
            var hero = FindHero(heroId);
            if (hero == null) return Task.FromResult(false);
            var cost = 50 * hero.Level;
            if (S.Money < cost) return Task.FromResult(false);
            S.Money -= cost;
            hero.Level++;
            hero.Xp = 0;
            hero.Stats.Health += 100;
            hero.Stats.Attack += 10;
            hero.Stats.Defense += 5;
            hero.Power += 50;
            return Task.FromResult(true);
        }

        public Task<bool> EquipItem(int heroId, int inventoryIndex)
        {
            var hero = FindHero(heroId);
            if (hero == null) return Task.FromResult(false);
            if (inventoryIndex < 0 || inventoryIndex >= S.Inventory.Count) return Task.FromResult(false);
            var item = S.Inventory[inventoryIndex];
            // Unequip whatever is currently in that slot (move back to inventory).
            for (int i = 0; i < hero.Equipped.Count; i++)
            {
                if (hero.Equipped[i].Slot == item.Slot)
                {
                    var prev = hero.Equipped[i];
                    hero.Equipped.RemoveAt(i);
                    S.Inventory.Add(new InventoryItem
                    {
                        ItemId = prev.ItemId, Name = prev.Name, Slot = prev.Slot,
                        Tier = prev.Tier, Level = prev.Level, Affixes = prev.Affixes,
                    });
                    break;
                }
            }
            hero.Equipped.Add(new EquippedItem
            {
                ItemId = item.ItemId, Slot = item.Slot, Name = item.Name,
                Tier = item.Tier, Level = item.Level, Affixes = item.Affixes,
            });
            hero.Power += 25 * item.Tier;
            S.Inventory.RemoveAt(inventoryIndex);
            return Task.FromResult(true);
        }

        public Task<bool> UnequipItem(int heroId, string slot)
        {
            var hero = FindHero(heroId);
            if (hero == null) return Task.FromResult(false);
            for (int i = 0; i < hero.Equipped.Count; i++)
            {
                if (hero.Equipped[i].Slot == slot)
                {
                    var eq = hero.Equipped[i];
                    S.Inventory.Add(new InventoryItem
                    {
                        ItemId = eq.ItemId, Name = eq.Name, Slot = eq.Slot,
                        Tier = eq.Tier, Level = eq.Level, Affixes = eq.Affixes,
                    });
                    hero.Equipped.RemoveAt(i);
                    hero.Power -= 25 * eq.Tier;
                    return Task.FromResult(true);
                }
            }
            return Task.FromResult(false);
        }

        public Task CompleteCampaignLevel(string levelId, int stars, int score)
        {
            if (string.IsNullOrEmpty(levelId)) return Task.CompletedTask;
            if (stars < 1) stars = 1; else if (stars > 3) stars = 3;
            var now = Context.ServerTimeTicks;
            for (int i = 0; i < S.CompletedLevels.Count; i++)
            {
                if (S.CompletedLevels[i].LevelId == levelId)
                {
                    var existing = S.CompletedLevels[i];
                    if (stars > existing.Stars) existing.Stars = stars;
                    if (score > existing.BestScore) existing.BestScore = score;
                    existing.Attempts++;
                    existing.LastPlayedTicks = now;
                    S.Money += 25 * stars;
                    return Task.CompletedTask;
                }
            }
            S.CompletedLevels.Add(new CompletedLevel
            {
                LevelId = levelId, Stars = stars, BestScore = score,
                Attempts = 1, LastPlayedTicks = now,
            });
            S.Money += 75 * stars;
            return Task.CompletedTask;
        }

        public Task OpenChest(int tier)
        {
            if (tier < 1) tier = 1;
            if (tier > 4) tier = 4;
            var seed = Context.Random!.Next(int.MaxValue);
            var chestId = S.NextEntityId++;
            var itemsAwarded = new List<int>();
            var dropCount = 3 + tier;
            for (int i = 0; i < dropCount; i++)
            {
                var item = GenerateItem(tier);
                itemsAwarded.Add(item.ItemId);
                S.Inventory.Add(item);
            }
            var money = 100 * tier * tier;
            S.Money += money;
            S.ChestHistory.Add(new ChestRecord
            {
                ChestId = chestId, Tier = tier,
                OpenedTicks = Context.ServerTimeTicks,
                ItemIdsAwarded = itemsAwarded,
                MoneyAwarded = money,
            });
            return Task.CompletedTask;
        }

        public Task ClaimDailyReward()
        {
            S.Money += 250;
            S.Resources["gems"] = S.Resources.TryGetValue("gems", out var g) ? g + 10 : 10;
            S.Resources["energy"] = S.Resources.TryGetValue("energy", out var e) ? e + 50 : 50;
            return Task.CompletedTask;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private HeroData? FindHero(int heroId)
        {
            for (int i = 0; i < S.Heroes.Count; i++)
                if (S.Heroes[i].HeroId == heroId) return S.Heroes[i];
            return null;
        }

        private InventoryItem GenerateItem(int tier)
        {
            var rng = Context.Random!;
            var slot = ItemSlots[rng.Next(ItemSlots.Length)];
            var prefix = ItemPrefixes[rng.Next(ItemPrefixes.Length)];
            var suffix = ItemSuffixes[rng.Next(ItemSuffixes.Length)];
            var name = $"{prefix} {Capitalize(slot)} {suffix}";
            var affixCount = 2 + tier;
            var affixes = new List<ItemAffix>(affixCount);
            for (int i = 0; i < affixCount; i++)
            {
                affixes.Add(new ItemAffix
                {
                    Stat = AffixStats[rng.Next(AffixStats.Length)],
                    Value = 5 + tier * 5 + rng.Next(10),
                    Tier = tier,
                });
            }
            return new InventoryItem
            {
                ItemId = S.NextEntityId++,
                Name = name,
                Slot = slot,
                Tier = tier,
                Level = 1,
                Affixes = affixes,
                Description = $"A {prefix.ToLowerInvariant()} {slot} forged in the deep places of the world. Bears {affixCount} ancient affixes, scaling with the tier-{tier} resonance.",
            };
        }

        private static string Capitalize(string s)
            => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // ── First-activation init: seed 5 heroes + 30 starter items ──────────────

        [MetaInit]
        public Task<int> Init(int version)
        {
            if (version >= 1) return Task.FromResult(version);
            // Seed the profile with a starter roster + inventory so the test exercises real
            // payload size from the first step. Idempotent on re-entry — the `version >= 1`
            // guard keeps this from rerunning after schema bumps.
            for (int i = 0; i < 5; i++)
            {
                var heroId = S.NextEntityId++;
                S.Heroes.Add(new HeroData
                {
                    HeroId = heroId,
                    Name = $"{HeroNames[heroId % HeroNames.Length]}-{heroId}",
                    Class = HeroClasses[i % HeroClasses.Length],
                    Level = 1 + i, Stars = 1, Power = 100 + i * 25,
                    Stats = new HeroStats
                    {
                        Health = 1000 + i * 200, Attack = 100 + i * 20, Defense = 50 + i * 10,
                        Speed = 20, CritChance = 5, CritDamage = 150, Resistance = 20, Accuracy = 100,
                    },
                    Skills = new List<string> { $"{HeroClasses[i % HeroClasses.Length]}-basic", $"{HeroClasses[i % HeroClasses.Length]}-passive" },
                });
            }
            for (int i = 0; i < 30; i++)
            {
                S.Inventory.Add(GenerateItem(1 + (i % 3)));
            }
            S.Resources["gems"]   = 100;
            S.Resources["energy"] = 100;
            return Task.FromResult(1);
        }
    }
}
