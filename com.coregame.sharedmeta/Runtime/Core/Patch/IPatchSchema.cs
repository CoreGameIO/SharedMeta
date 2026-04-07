using System;
using System.Collections.Generic;

namespace SharedMeta.Core.Patch
{
    /// <summary>
    /// Compile-time schema for an <see cref="ISharedState"/> type. Used by
    /// <see cref="PatchTextRenderer"/> to render <see cref="PatchNode"/> trees
    /// as human-readable JSON with proper field names and typed values.
    ///
    /// One implementation per state class is generated alongside the state's
    /// PatchWrapper. Schemas are inert at runtime — they are only consulted
    /// from diagnostic / desync-reporting paths, never from hot RPC code.
    /// </summary>
    public interface IPatchSchema
    {
        /// <summary>Resolve a field id (from [MemoryPackOrder] / [Id] / [Key]) to its declared property name.</summary>
        string GetFieldName(int fieldId);

        /// <summary>
        /// Decode a terminal patch value back into a typed object using the field's declared type.
        /// Used by the renderer to format the value as JSON.
        /// Returns the raw bytes if the field id is unknown.
        /// </summary>
        object? DecodeLeaf(int fieldId, byte[] bytes, IMetaSerializer serializer);

        /// <summary>
        /// For SubWrappable fields (nested ISharedState), return the schema of the nested type.
        /// Returns null for terminal / collection fields.
        /// </summary>
        IPatchSchema? GetNestedSchema(int fieldId);
    }

    /// <summary>
    /// Lookup of <see cref="IPatchSchema"/> instances by state type or service name.
    /// Generated and registered in DI by <c>ServerMetaConfigurationGenerator</c>.
    /// </summary>
    public interface IPatchSchemaRegistry
    {
        /// <summary>Lookup by fully qualified state type name (e.g. "Expedition.Shared.ExpeditionState").</summary>
        IPatchSchema? GetByStateTypeName(string stateTypeFullName);

        /// <summary>Lookup by service interface name (e.g. "IExpeditionService"). Convenience for desync handlers that only know the service.</summary>
        IPatchSchema? GetByServiceName(string serviceName);
    }

    /// <summary>
    /// Simple dictionary-backed registry. Generator emits a static singleton populated at startup.
    /// </summary>
    public sealed class PatchSchemaRegistry : IPatchSchemaRegistry
    {
        private readonly Dictionary<string, IPatchSchema> _byState;
        private readonly Dictionary<string, IPatchSchema> _byService;

        public PatchSchemaRegistry(
            Dictionary<string, IPatchSchema> byState,
            Dictionary<string, IPatchSchema> byService)
        {
            _byState = byState ?? throw new ArgumentNullException(nameof(byState));
            _byService = byService ?? throw new ArgumentNullException(nameof(byService));
        }

        public IPatchSchema? GetByStateTypeName(string stateTypeFullName)
            => _byState.TryGetValue(stateTypeFullName, out var s) ? s : null;

        public IPatchSchema? GetByServiceName(string serviceName)
            => _byService.TryGetValue(serviceName, out var s) ? s : null;
    }
}
