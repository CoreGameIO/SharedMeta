using System;

namespace SharedMeta.Core
{
    /// <summary>
    /// Writes multiple values into a payload sequentially.
    /// </summary>
    public interface IPayloadWriter : IDisposable
    {
        /// <summary>
        /// Write a value to the payload.
        /// </summary>
        void Write<T>(T value);

        /// <summary>
        /// Finalize and return the completed payload as bytes.
        /// </summary>
        byte[] Complete();
    }

    /// <summary>
    /// Reads values from a payload in order.
    /// </summary>
    public interface IPayloadReader : IDisposable
    {
        /// <summary>
        /// Read the next value of type T.
        /// </summary>
        T Read<T>();

        /// <summary>
        /// Read the next value as raw bytes (for deferred deserialization).
        /// Used by argument transformers to read boxed values.
        /// </summary>
        byte[] ReadRaw();

        /// <summary>
        /// True if more values remain to be read.
        /// </summary>
        bool HasMore { get; }
    }

    /// <summary>
    /// Universal serializer supporting multi-value payloads.
    /// All methods work directly with byte[] - no IPayload abstraction.
    /// </summary>
    public interface IMetaSerializer
    {
        // Multi-value operations
        IPayloadWriter CreateWriter();
        IPayloadReader CreateReader(byte[] data);

        // Single-value shortcuts
        byte[] Pack<T>(T value);
        T Unpack<T>(byte[] data);

        // Runtime type support (for reflection scenarios)
        byte[] Pack(Type type, object value);
        object? Unpack(Type type, byte[] data);

        // RpcCall serialization
        byte[] SerializeRpcCall(RpcCall call);
        RpcCall DeserializeRpcCall(byte[] data);

        // Deep copy via serialization
        T Clone<T>(T value);
    }
}
