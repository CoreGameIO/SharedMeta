using SharedMeta.Core;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Test.Meta2.Generic;
using SharedMeta.Test.Meta2.Generic.Server;
using Xunit;
using Ids = global::SharedMeta.Test.Meta2.Generic.Generated.GameMethodIds;

namespace SharedMeta.GenericSerializerTests;

/// <summary>
/// The generated method table on the generic wire. This project references MemoryPack while the
/// service assembly declares the generic serializer, so a codec keyed to the wrong assembly would
/// frame MemoryPack-style and fail here.
/// </summary>
[Collection(GenericClusterCollection.Name)]
public class MethodDescriptorTests
{
    private static readonly GeneratedMetaMethodDescriptors Descriptors = GeneratedMetaMethodDescriptors.Instance;

    private readonly GenericClusterFixture _fixture;

    public MethodDescriptorTests(GenericClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void TransformedParameter_IsDescribedByItsWireType()
    {
        var move = Descriptors.Get(Ids.IGenericService_MoveMixed_v0)!;

        Assert.Equal(new[] { "lead", "position", "tag" }, move.Parameters.Select(p => p.Name));
        Assert.False(move.Parameters[0].IsTransformed);
        Assert.True(move.Parameters[1].IsTransformed);
        Assert.Equal(typeof(int), move.Parameters[1].WireType);
        Assert.Equal(typeof(string), move.ResultType);

        var payload = Descriptors.EncodeArgs(move.MethodId, new object?[] { 9, 2004, 13 }, _fixture.Serializer);
        Assert.Equal(new object?[] { 9, 2004, 13 }, Descriptors.DecodeArgs(move.MethodId, payload, _fixture.Serializer));
    }

    /// <summary>The encoded payload is exactly what the dispatcher reads: it unboxes the wire value itself.</summary>
    [Fact(Timeout = 60_000)]
    public async Task EncodedArguments_AreAcceptedByTheDispatcher()
    {
        var entityId = $"generic_desc_{Guid.NewGuid():N}";
        var methodId = Ids.IGenericService_AdminMove_v0;

        var grain = _fixture.Cluster.GrainFactory.GetGrain<IEntityGrain<GenericState>>(entityId);
        var result = await grain.HandleCallFromEntityAsync(new RpcCall
        {
            MethodId = methodId,
            // Point { X = 2, Y = 4 } boxed by PointTransformer, then the trailing tag.
            Payload = Descriptors.EncodeArgs(methodId, new object?[] { 2004, 13 }, _fixture.Serializer),
            ServerTimeTicks = DateTime.UtcNow.Ticks,
        });

        Assert.False(result.HasError, result.Error);
        Assert.Equal("2:4:unboxed:13", Descriptors.DecodeResult(methodId, result.ResultBytes, _fixture.Serializer));
    }
}
