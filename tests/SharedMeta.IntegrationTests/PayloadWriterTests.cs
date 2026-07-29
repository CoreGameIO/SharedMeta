using SharedMeta.Serialization.MemoryPack;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// The length-prefixed <c>IPayloadWriter</c> path — what recorders and the generic-serializer
/// dispatch branch write through.
/// </summary>
public class PayloadWriterTests
{
    /// <summary>
    /// Regression: the 4-byte length prefix was reserved by advancing the write index before
    /// growing, so a prefix landing in the last 3 bytes of the buffer pushed the index past the
    /// end. The next growth then copied <c>AsSpan(0, _index)</c> out of range and threw. Needs
    /// enough writes to reach a growth boundary, which is why a recorder with a few dozen
    /// entries hit it and every short payload did not.
    /// </summary>
    [Fact]
    public void Writer_GrowsAcrossBufferBoundary_WithoutOverrunningIndex()
    {
        var serializer = new MemoryPackMetaSerializer();

        // Ints are 4 bytes of prefix + a small body, so the prefix walks over every alignment
        // relative to the 256-byte initial rent well before this count.
        const int count = 512;
        using var writer = serializer.CreateWriter();
        for (int i = 0; i < count; i++)
            writer.Write(i);

        var bytes = writer.Complete().ToArray();

        // Read back rather than just asserting no throw: an index that ran past the buffer could
        // also have silently truncated a prefix.
        using var reader = serializer.CreateReader(bytes);
        for (int i = 0; i < count; i++)
            Assert.Equal(i, reader.Read<int>());
    }

    /// <summary>
    /// Same boundary, reached with variable-length values so the prefix lands at offsets a
    /// fixed-size loop never produces.
    /// </summary>
    [Fact]
    public void Writer_VariableLengthValues_RoundTrip()
    {
        var serializer = new MemoryPackMetaSerializer();
        var values = Enumerable.Range(0, 200).Select(i => new string('x', i % 37)).ToList();

        using var writer = serializer.CreateWriter();
        foreach (var value in values)
            writer.Write(value);

        var bytes = writer.Complete().ToArray();

        using var reader = serializer.CreateReader(bytes);
        foreach (var expected in values)
            Assert.Equal(expected, reader.Read<string>());
    }
}
