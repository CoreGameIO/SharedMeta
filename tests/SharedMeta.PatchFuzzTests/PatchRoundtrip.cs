using System;
using System.Collections.Generic;
using SharedMeta.Core;
using SharedMeta.Core.Patch;
using SharedMeta.Serialization.MemoryPack;
using Xunit;

namespace SharedMeta.PatchFuzzTests;

/// <summary>
/// Shared roundtrip harness for the patch fuzz suite.
///
/// Each test follows the same pattern:
///   1. Build an "original" FuzzState (the server-side state).
///   2. Deep-copy it into "copy" (the client-side state, before the call).
///   3. Run a mutation lambda against the original *through a PatchWrapper*,
///      which records changes onto a fresh PatchNode tree.
///   4. Prune empty branches, serialize the patch, deserialize it (so any
///      MemoryPack roundtrip issues surface), and apply it to "copy" via
///      the generated FuzzStatePatchApplier.
///   5. Assert that "original" and "copy" are byte-for-byte identical when
///      reserialized — i.e. the patch correctly described every mutation
///      and the applier reproduced it exactly on the receiving side.
///
/// Tests use this harness via two entrypoints:
///
///   - <see cref="AssertRoundtrip(System.Action{FuzzStatePatchWrapper})"/>
///     for cases where the original starts as a fresh empty state.
///   - <see cref="AssertRoundtrip(FuzzState, System.Action{FuzzStatePatchWrapper})"/>
///     for cases where the original needs preset data (most list tests
///     pre-populate, then mutate).
/// </summary>
public static class PatchRoundtrip
{
    public static readonly IMetaSerializer Serializer = new MemoryPackMetaSerializer();

    public static void AssertRoundtrip(Action<FuzzStatePatchWrapper> mutate)
        => AssertRoundtrip(new FuzzState(), mutate);

    public static void AssertRoundtrip(FuzzState original, Action<FuzzStatePatchWrapper> mutate)
    {
        // Receiver-side starting state — bytewise identical to "original" before the call.
        var copy = DeepCopy(original);

        // Build the patch on the sender side.
        var root = new PatchNode { FieldId = -1 };
        var wrapper = new FuzzStatePatchWrapper(original, root, Serializer);
        mutate(wrapper);
        root.Prune();

        // Serialize → deserialize the patch so any wire-format issues surface.
        var patchBytes = Serializer.Pack(root);
        var deserialized = Serializer.Unpack<PatchNode>(patchBytes)!;

        // Apply on the receiver side.
        FuzzStatePatchApplier.Apply(copy, deserialized, Serializer);

        // States must converge.
        AssertStatesEqual(original, copy);
    }

    /// <summary>
    /// Run two independent mutation lambdas against two fresh copies of the
    /// same starting state and verify both reach byte-identical results — used
    /// when an "applied via wrapper" path and a "would-be received state" path
    /// should agree (server-vs-client convergence test without the patch hop).
    /// </summary>
    public static void AssertConvergence(FuzzState seed, Action<FuzzStatePatchWrapper> serverSide, Action<FuzzState> clientSide)
    {
        var server = DeepCopy(seed);
        var root = new PatchNode { FieldId = -1 };
        var wrapper = new FuzzStatePatchWrapper(server, root, Serializer);
        serverSide(wrapper);

        var client = DeepCopy(seed);
        clientSide(client);

        AssertStatesEqual(server, client);
    }

    public static FuzzState DeepCopy(FuzzState state)
        => Serializer.Unpack<FuzzState>(Serializer.Pack(state))!;

    public static void AssertStatesEqual(FuzzState a, FuzzState b)
    {
        var ab = Serializer.Pack(a);
        var bb = Serializer.Pack(b);
        if (!ByteSpanEquals(ab, bb))
        {
            throw new Xunit.Sdk.XunitException(
                $"States differ after roundtrip.\nExpected ({ab.Length} bytes): {Hex(ab)}\nActual   ({bb.Length} bytes): {Hex(bb)}");
        }
    }

    private static bool ByteSpanEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static string Hex(byte[] bytes)
    {
        if (bytes.Length > 256)
            return BitConverter.ToString(bytes, 0, 256).Replace("-", "") + "…";
        return BitConverter.ToString(bytes).Replace("-", "");
    }

    // ─────────────────────────────────────────────────────────────────
    // State builders shared across test files
    // ─────────────────────────────────────────────────────────────────

    public static FuzzState SeedSimple()
    {
        return new FuzzState
        {
            Counter = 10,
            Timestamp = 1_700_000_000_000,
            Name = "seed",
            Active = true,
            Kind = FuzzKind.Alpha,
            Blob = new byte[] { 1, 2, 3, 4 },
            RequiredProfile = new Profile { DisplayName = "Player", Level = 1 },
        };
    }

    public static FuzzState SeedWithHeroes(int heroCount, int itemsPerHero = 0)
    {
        var s = SeedSimple();
        for (int i = 0; i < heroCount; i++)
        {
            var hero = new Hero
            {
                Id = i + 1,
                Name = $"Hero{i + 1}",
                Level = (i + 1) * 5,
                Hp = 100,
                Exp = 0,
                Stats = new Stats { Hp = 100, Mp = 50, Strength = 10 + i, Agility = 5 },
            };
            for (int j = 0; j < itemsPerHero; j++)
            {
                hero.Equipment.Add(new Item
                {
                    ItemId = (i + 1) * 100 + j,
                    Name = $"Item{j + 1}",
                    Power = 10 + j,
                    Tags = new List<string> { $"tag-{j}-a", $"tag-{j}-b" },
                });
            }
            for (int k = 0; k < 3; k++) hero.SkillIds.Add((i + 1) * 10 + k);
            s.Heroes.Add(hero);
        }
        return s;
    }

    public static FuzzState SeedWithNumbers(params int[] numbers)
    {
        var s = SeedSimple();
        s.Numbers.AddRange(numbers);
        return s;
    }

    public static FuzzState SeedWithTags(params string[] tags)
    {
        var s = SeedSimple();
        s.Tags.AddRange(tags);
        return s;
    }

    public static FuzzState SeedWithBuffer(params byte[] buffer)
    {
        var s = SeedSimple();
        s.Buffer.AddRange(buffer);
        return s;
    }

    public static FuzzState SeedWithCounters(params (string key, int value)[] entries)
    {
        var s = SeedSimple();
        foreach (var (k, v) in entries) s.Counters[k] = v;
        return s;
    }

    public static FuzzState SeedWithFlags(params int[] flags)
    {
        var s = SeedSimple();
        foreach (var f in flags) s.Flags.Add(f);
        return s;
    }
}
