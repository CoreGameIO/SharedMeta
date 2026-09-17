using System.Collections.Generic;
using System.Reflection;
using MessagePack;
using MessagePack.Resolvers;

namespace SharedMeta.Serialization.MessagePack
{
    /// <summary>
    /// Shared MessagePackSerializerOptions for the SharedMeta framework.
    ///
    /// For multi-assembly projects, call <see cref="Configure(Assembly[])"/> at startup
    /// to compose source-generated resolvers from all assemblies that contain
    /// [MessagePackObject] types.
    ///
    /// Used by MessagePackMetaSerializer (payload serialization).
    /// </summary>
    public static class MetaMessagePackOptions
    {
        private static MessagePackSerializerOptions? _configured;

        /// <summary>
        /// Configured MessagePack options.
        /// Returns configured composite resolver if <see cref="Configure(Assembly[])"/> was called,
        /// otherwise falls back to StandardResolverAllowPrivate.
        /// </summary>
        public static MessagePackSerializerOptions Instance =>
            _configured ?? DefaultOptions;

        private static readonly MessagePackSerializerOptions DefaultOptions =
            MessagePackSerializerOptions.Standard
                .WithResolver(StandardResolverAllowPrivate.Instance)
                .WithSecurity(MessagePackSecurity.UntrustedData);

        /// <summary>
        /// Configure MessagePack with source-generated resolvers discovered from the specified assemblies.
        /// Each assembly containing [MessagePackObject] types will have its GeneratedMessagePackResolver
        /// composed into a single CompositeResolver. StandardResolverAllowPrivate is added as fallback.
        ///
        /// Call this at startup before creating MessagePackMetaSerializer.
        /// Order matters: resolvers are searched first-to-last. Put game assemblies first.
        /// </summary>
        public static void Configure(params Assembly[] assembliesWithMessagePackTypes)
        {
            var resolvers = new List<IFormatterResolver>();

            foreach (var assembly in assembliesWithMessagePackTypes)
            {
                var resolver = GetGeneratedResolver(assembly);
                if (resolver != null)
                    resolvers.Add(resolver);
            }

            // Standard resolver as fallback (handles built-in types, enums, collections, etc.)
            resolvers.Add(StandardResolverAllowPrivate.Instance);

            _configured = MessagePackSerializerOptions.Standard
                .WithResolver(CompositeResolver.Create(resolvers.ToArray()))
                .WithSecurity(MessagePackSecurity.UntrustedData);
        }

        /// <summary>
        /// Configure MessagePack with explicit resolvers.
        /// StandardResolverAllowPrivate is NOT added automatically — include it if needed.
        /// </summary>
        public static void Configure(params IFormatterResolver[] resolvers)
        {
            _configured = MessagePackSerializerOptions.Standard
                .WithResolver(CompositeResolver.Create(resolvers))
                .WithSecurity(MessagePackSecurity.UntrustedData);
        }

        /// <summary>
        /// Discovers the source-generated MessagePack resolver from an assembly.
        /// MessagePack's source generator creates MessagePack.GeneratedMessagePackResolver
        /// in each assembly that contains [MessagePackObject] types.
        /// </summary>
        private static IFormatterResolver? GetGeneratedResolver(Assembly assembly)
        {
            var resolverType = assembly.GetType("MessagePack.GeneratedMessagePackResolver");
            if (resolverType == null) return null;
            var field = resolverType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
            return field?.GetValue(null) as IFormatterResolver;
        }
    }
}
