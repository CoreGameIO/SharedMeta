using Xunit;
using static SharedMeta.PatchFuzzTests.PatchRoundtrip;

namespace SharedMeta.PatchFuzzTests;

/// <summary>
/// Terminal scalar field mutations on the root state.
/// Each test mutates a single field through the wrapper and verifies that
/// the receiving copy converges to byte-identical state.
/// </summary>
public class ScalarTests
{
    // ── int ──────────────────────────────────────────────────────────
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(42)]
    [InlineData(-1_000_000)]
    public void Counter_Set(int value)
        => AssertRoundtrip(SeedSimple(), w => w.Counter = value);

    [Fact]
    public void Counter_SetTwiceSameValue_StillRoundtrips()
        => AssertRoundtrip(SeedSimple(), w => { w.Counter = 7; w.Counter = 7; });

    [Fact]
    public void Counter_SetTwiceDifferentValues_LastWins()
        => AssertRoundtrip(SeedSimple(), w => { w.Counter = 7; w.Counter = 99; });

    [Fact]
    public void Counter_SetThenSetBackToSeedValue()
        => AssertRoundtrip(SeedSimple(), w => { w.Counter = 999; w.Counter = 10; });

    // ── long ─────────────────────────────────────────────────────────
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(1_700_000_000_000L)]
    public void Timestamp_Set(long value)
        => AssertRoundtrip(SeedSimple(), w => w.Timestamp = value);

    // ── string ───────────────────────────────────────────────────────
    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("a")]
    [InlineData("with spaces and punctuation!")]
    [InlineData("\n\t\r\\")]
    [InlineData("Юникод и эмодзи 🎮")]
    [InlineData("very-long-name-with-some-padding-1234567890-1234567890-1234567890")]
    public void Name_Set(string value)
        => AssertRoundtrip(SeedSimple(), w => w.Name = value);

    [Fact]
    public void Name_SetEmptyOverNonEmpty()
        => AssertRoundtrip(SeedSimple(), w => w.Name = "");

    // ── bool ─────────────────────────────────────────────────────────
    [Fact]
    public void Active_SetTrue()
        => AssertRoundtrip(new FuzzState(), w => w.Active = true);

    [Fact]
    public void Active_SetFalse()
        => AssertRoundtrip(SeedSimple(), w => w.Active = false);

    [Fact]
    public void Active_Toggle()
        => AssertRoundtrip(SeedSimple(), w => { w.Active = false; w.Active = true; });

    // ── enum ─────────────────────────────────────────────────────────
    [Theory]
    [InlineData(FuzzKind.None)]
    [InlineData(FuzzKind.Alpha)]
    [InlineData(FuzzKind.Beta)]
    [InlineData(FuzzKind.Gamma)]
    public void Kind_Set(FuzzKind value)
        => AssertRoundtrip(SeedSimple(), w => w.Kind = value);

    // ── byte[] ───────────────────────────────────────────────────────
    [Fact]
    public void Blob_SetEmpty()
        => AssertRoundtrip(SeedSimple(), w => w.Blob = System.Array.Empty<byte>());

    [Fact]
    public void Blob_SetSingleByte()
        => AssertRoundtrip(SeedSimple(), w => w.Blob = new byte[] { 0xFF });

    [Fact]
    public void Blob_SetSmall()
        => AssertRoundtrip(SeedSimple(), w => w.Blob = new byte[] { 1, 2, 3, 4, 5 });

    [Fact]
    public void Blob_SetLarge()
    {
        var bytes = new byte[1024];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
        AssertRoundtrip(SeedSimple(), w => w.Blob = bytes);
    }

    [Fact]
    public void Blob_SetAllZeros()
        => AssertRoundtrip(SeedSimple(), w => w.Blob = new byte[64]);

    // ── Multiple scalars in one patch ────────────────────────────────
    [Fact]
    public void Multiple_AllScalars_OneCall()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Counter = 555;
            w.Timestamp = 999_999_999;
            w.Name = "renamed";
            w.Active = false;
            w.Kind = FuzzKind.Gamma;
            w.Blob = new byte[] { 9, 8, 7 };
        });

    [Fact]
    public void Multiple_TwoScalars_PatchHasTwoChildren()
        => AssertRoundtrip(SeedSimple(), w => { w.Counter = 1; w.Timestamp = 2; });

    [Fact]
    public void Multiple_SameScalarSetTwice_OnlyLastVisible()
        => AssertRoundtrip(SeedSimple(), w => { w.Counter = 1; w.Counter = 2; w.Counter = 3; });

    [Fact]
    public void Multiple_AlternatingScalars()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Counter = 1;
            w.Timestamp = 1;
            w.Counter = 2;
            w.Timestamp = 2;
            w.Counter = 3;
            w.Timestamp = 3;
        });
}
