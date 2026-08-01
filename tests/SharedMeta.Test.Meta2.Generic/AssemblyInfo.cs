using SharedMeta.Core;

// The reason this assembly exists. SharedMeta.Test.Meta1 references MemoryPack and therefore
// resolves to the MemoryPack codegen branch, leaving the generic IPayloadWriter/IPayloadReader
// branch with no coverage at all — every wire bug found in the 0.37.0 transformer work was
// serializer-specific, and none of them could have been caught from Meta1.
//
// Separate assembly rather than a second service inside Meta1: GameMethodIds are sequential
// ushorts assigned per assembly from zero, and a session carries one ServerSignature, so two
// meta assemblies cannot share a cluster. Hence this project plus its own test host.
[assembly: MetaSerializer(SerializerType.Generic)]
