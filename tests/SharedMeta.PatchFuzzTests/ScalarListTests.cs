using System.Collections.Generic;
using Xunit;
using static SharedMeta.PatchFuzzTests.PatchRoundtrip;

namespace SharedMeta.PatchFuzzTests;

/// <summary>
/// Structural ops on scalar lists at the root: <c>Numbers</c> (List&lt;int&gt;),
/// <c>Tags</c> (List&lt;string&gt;), <c>Buffer</c> (List&lt;byte&gt;).
///
/// All three use <c>PatchableList&lt;T&gt;</c>, so any failure on one likely
/// applies to the others. Tests are parameterized aggressively to cover edge
/// indices (head, tail, middle, last+1) and edge sizes (empty, 1, many).
/// </summary>
public class ScalarListTests
{
    // ── Numbers (List<int>) ──────────────────────────────────────────
    [Fact]
    public void Numbers_AddOne_FromEmpty()
        => AssertRoundtrip(SeedSimple(), w => w.Numbers.Add(42));

    [Fact]
    public void Numbers_AddMany_FromEmpty()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            for (int i = 0; i < 20; i++) w.Numbers.Add(i * i);
        });

    [Fact]
    public void Numbers_AddOne_OntoExisting()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3), w => w.Numbers.Add(99));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Numbers_InsertAt(int index)
        => AssertRoundtrip(SeedWithNumbers(10, 20, 30), w => w.Numbers.Insert(index, 999));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Numbers_RemoveAt(int index)
        => AssertRoundtrip(SeedWithNumbers(10, 20, 30), w => w.Numbers.RemoveAt(index));

    [Fact]
    public void Numbers_RemoveByValue_Existing()
        => AssertRoundtrip(SeedWithNumbers(10, 20, 30, 40), w => w.Numbers.Remove(20));

    [Fact]
    public void Numbers_RemoveByValue_FirstOnly()
        => AssertRoundtrip(SeedWithNumbers(10, 20, 10, 20), w => w.Numbers.Remove(10));

    [Fact]
    public void Numbers_RemoveByValue_NotFound_Noop()
        => AssertRoundtrip(SeedWithNumbers(10, 20, 30), w => w.Numbers.Remove(999));

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 200)]
    [InlineData(2, 300)]
    public void Numbers_IndexerSet(int index, int value)
        => AssertRoundtrip(SeedWithNumbers(10, 20, 30), w => w.Numbers[index] = value);

    [Fact]
    public void Numbers_IndexerSet_AllElements()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3, 4, 5), w =>
        {
            for (int i = 0; i < 5; i++) w.Numbers[i] = i * 100;
        });

    [Fact]
    public void Numbers_Clear_Empty()
        => AssertRoundtrip(SeedSimple(), w => w.Numbers.Clear());

    [Fact]
    public void Numbers_Clear_NonEmpty()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3, 4, 5), w => w.Numbers.Clear());

    [Fact]
    public void Numbers_AddRange()
        => AssertRoundtrip(SeedWithNumbers(1, 2), w => w.Numbers.AddRange(new[] { 3, 4, 5 }));

    [Fact]
    public void Numbers_Sort()
        => AssertRoundtrip(SeedWithNumbers(5, 2, 8, 1, 9, 3), w => w.Numbers.Sort());

    [Fact]
    public void Numbers_Reverse()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3, 4, 5), w => w.Numbers.Reverse());

    [Fact]
    public void Numbers_RemoveAll_EvenValues()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3, 4, 5, 6), w => w.Numbers.RemoveAll(n => n % 2 == 0));

    // ── Numbers compositions ─────────────────────────────────────────
    [Fact]
    public void Numbers_AddThenRemoveAt()
        => AssertRoundtrip(SeedWithNumbers(1, 2), w => { w.Numbers.Add(3); w.Numbers.RemoveAt(0); });

    [Fact]
    public void Numbers_InsertThenRemove()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3), w => { w.Numbers.Insert(1, 99); w.Numbers.RemoveAt(2); });

    [Fact]
    public void Numbers_AddThenSet()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3), w => { w.Numbers.Add(4); w.Numbers[3] = 999; });

    [Fact]
    public void Numbers_ClearThenAdd()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3), w => { w.Numbers.Clear(); w.Numbers.Add(99); });

    [Fact]
    public void Numbers_ClearThenAddMany()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3), w =>
        {
            w.Numbers.Clear();
            for (int i = 0; i < 5; i++) w.Numbers.Add(i);
        });

    [Fact]
    public void Numbers_RemoveAllThenAdd()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3, 4), w =>
        {
            w.Numbers.RemoveAll(n => n > 2);
            w.Numbers.Add(99);
        });

    // ── Tags (List<string>) ──────────────────────────────────────────
    [Fact]
    public void Tags_AddOne()
        => AssertRoundtrip(SeedSimple(), w => w.Tags.Add("first"));

    [Fact]
    public void Tags_AddMany()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Tags.Add("a");
            w.Tags.Add("b");
            w.Tags.Add("c");
        });

    [Fact]
    public void Tags_InsertAtHead()
        => AssertRoundtrip(SeedWithTags("b", "c"), w => w.Tags.Insert(0, "a"));

    [Fact]
    public void Tags_InsertAtTail()
        => AssertRoundtrip(SeedWithTags("a", "b"), w => w.Tags.Insert(2, "c"));

    [Fact]
    public void Tags_RemoveAt_First()
        => AssertRoundtrip(SeedWithTags("a", "b", "c"), w => w.Tags.RemoveAt(0));

    [Fact]
    public void Tags_RemoveAt_Last()
        => AssertRoundtrip(SeedWithTags("a", "b", "c"), w => w.Tags.RemoveAt(2));

    [Fact]
    public void Tags_RemoveByValue()
        => AssertRoundtrip(SeedWithTags("a", "b", "c"), w => w.Tags.Remove("b"));

    [Fact]
    public void Tags_IndexerSet()
        => AssertRoundtrip(SeedWithTags("a", "b", "c"), w => w.Tags[1] = "B");

    [Fact]
    public void Tags_Clear()
        => AssertRoundtrip(SeedWithTags("a", "b", "c"), w => w.Tags.Clear());

    [Fact]
    public void Tags_Sort()
        => AssertRoundtrip(SeedWithTags("c", "a", "b"), w => w.Tags.Sort());

    [Fact]
    public void Tags_Reverse()
        => AssertRoundtrip(SeedWithTags("a", "b", "c"), w => w.Tags.Reverse());

    [Fact]
    public void Tags_AddRange()
        => AssertRoundtrip(SeedWithTags("a"), w => w.Tags.AddRange(new[] { "b", "c", "d" }));

    [Fact]
    public void Tags_AddSpecialChars()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Tags.Add("");
            w.Tags.Add("\n\t\r");
            w.Tags.Add("Юникод 🎮");
            w.Tags.Add("a,b,c;d");
        });

    // ── Buffer (List<byte>) — same code path as Cells in user's bug ──
    [Fact]
    public void Buffer_AddOne()
        => AssertRoundtrip(SeedSimple(), w => w.Buffer.Add(0xAB));

    [Fact]
    public void Buffer_AddMany()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            for (int i = 0; i < 64; i++) w.Buffer.Add((byte)i);
        });

    [Fact]
    public void Buffer_InsertAtHead()
        => AssertRoundtrip(SeedWithBuffer(1, 2, 3), w => w.Buffer.Insert(0, 99));

    [Fact]
    public void Buffer_RemoveAt()
        => AssertRoundtrip(SeedWithBuffer(1, 2, 3, 4, 5), w => w.Buffer.RemoveAt(2));

    [Fact]
    public void Buffer_IndexerSet_AcrossEntireBuffer()
        => AssertRoundtrip(SeedWithBuffer(0, 0, 0, 0, 0, 0, 0, 0), w =>
        {
            for (int i = 0; i < 8; i++) w.Buffer[i] = (byte)(i + 1);
        });

    [Fact]
    public void Buffer_Clear()
        => AssertRoundtrip(SeedWithBuffer(1, 2, 3, 4, 5), w => w.Buffer.Clear());

    [Fact]
    public void Buffer_AddRange_Large()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            var bytes = new byte[256];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i ^ 0xAA);
            w.Buffer.AddRange(bytes);
        });

    // ── Mixed structural ops on scalar lists ─────────────────────────
    [Fact]
    public void Numbers_LongOpStream()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Numbers.Add(1);
            w.Numbers.Add(2);
            w.Numbers.Add(3);
            w.Numbers.Insert(1, 99);
            w.Numbers.RemoveAt(0);
            w.Numbers[1] = 88;
            w.Numbers.Add(77);
        });

    [Fact]
    public void Tags_LongOpStream()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Tags.Add("a");
            w.Tags.Add("b");
            w.Tags.Insert(0, "x");
            w.Tags.RemoveAt(2);
            w.Tags[0] = "X";
        });

    [Fact]
    public void Buffer_LongOpStream()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            for (byte b = 0; b < 16; b++) w.Buffer.Add(b);
            w.Buffer.RemoveAt(0);
            w.Buffer.Insert(5, 0xFF);
            w.Buffer[10] = 0xEE;
            w.Buffer.RemoveAt(15);
        });
}
