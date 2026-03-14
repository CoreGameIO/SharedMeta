using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Patch;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Transport;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Server;

namespace SharedMeta.Test.Server;

/// <summary>
/// Test server configuration - manually registers meta services.
/// This is needed because the generator scans referenced assemblies,
/// but we're linking source files for the test server.
/// </summary>
public static class TestServerConfiguration
{
    /// <summary>
    /// Register all test meta services and provider factories.
    /// </summary>
    public static IServiceCollection ConfigureTestMeta(
        this IServiceCollection services,
        Action<IServiceCollection>? configureServices = null)
    {
        // Configure server-side services if needed
        configureServices?.Invoke(services);

        // Register provider factory for CounterState
        services.AddSingleton<IMetaProviderFactory<CounterState>>(sp =>
            new GeneratedCounterStateMetaProviderFactory(
                t => sp.GetRequiredService(t),
                entityCallHandler: null));

        // Register entity grain resolver
        services.AddSingleton<IEntityGrainResolver>(new GeneratedEntityGrainResolver());

        // Register MetaConnectionHandlerFactory with signature validator
        services.AddSingleton<IMetaConnectionHandlerFactory>(sp =>
            new MetaConnectionHandlerFactory(
                sp.GetRequiredService<Orleans.IGrainFactory>(),
                sp.GetRequiredService<ILoggerFactory>(),
                MetaMethodSignatureValidator.ValidateClientSignatures));

        return services;
    }
}

/// <summary>
/// Generated MetaProvider for CounterState.
/// Inherits common functionality from MetaProviderBase, only implements dispatch logic.
/// </summary>
public sealed class GeneratedCounterStateMetaProvider : MetaProviderBase<CounterState>
{
    private ICounterService? _counterService;

    public GeneratedCounterStateMetaProvider(
        Func<Type, object>? serviceResolver = null,
        Func<string, string, string, byte[], long, System.Threading.Tasks.Task<SharedMeta.Server.CrossEntityCallInfo>>? entityCallHandler = null)
    {
        ServiceResolver = serviceResolver;
        EntityCallHandler = entityCallHandler;
    }

    public override void OnDeactivating()
    {
        _counterService = null;
    }

    protected override async System.Threading.Tasks.Task<DispatchResult> DispatchCall(string serviceName, string methodName, byte[] payload)
    {
        return serviceName switch
        {
            "ICounterService" => await ICounterServiceDispatcher.Dispatch(GetCounterService(), methodName, payload, Context!.Serializer),
            _ => throw new InvalidOperationException($"Unknown service: {serviceName}")
        };
    }

    protected override System.Threading.Tasks.Task<DispatchResult> DispatchEvent(
        string subscriberInterface, string methodName, byte[] eventData)
    {
        // CounterService has no subscriber interfaces
        return System.Threading.Tasks.Task.FromResult(new DispatchResult { ResultBytes = null });
    }

    protected override object? CreatePatchWrapper(PatchNode root)
    {
        return new CounterStatePatchWrapper(State, root, Context.Serializer);
    }

    protected override async System.Threading.Tasks.Task<int> RunInitAsync(int currentVersion)
    {
        var version = currentVersion;
        version = await ((CounterService)GetCounterService()).Init(version);
        return version;
    }

    private ICounterService GetCounterService() => _counterService ??= new CounterService();
}

/// <summary>
/// Factory for GeneratedCounterStateMetaProvider.
/// </summary>
public sealed class GeneratedCounterStateMetaProviderFactory : IMetaProviderFactory<CounterState>
{
    private readonly Func<Type, object>? _serviceResolver;
    private readonly Func<string, string, string, byte[], long, System.Threading.Tasks.Task<SharedMeta.Server.CrossEntityCallInfo>>? _entityCallHandler;

    public GeneratedCounterStateMetaProviderFactory(
        Func<Type, object>? serviceResolver = null,
        Func<string, string, string, byte[], long, System.Threading.Tasks.Task<SharedMeta.Server.CrossEntityCallInfo>>? entityCallHandler = null)
    {
        _serviceResolver = serviceResolver;
        _entityCallHandler = entityCallHandler;
    }

    public IMetaProvider<CounterState> Create()
    {
        return new GeneratedCounterStateMetaProvider(_serviceResolver, _entityCallHandler);
    }
}

/// <summary>
/// Server-side method signature validation.
/// </summary>
public static class MetaMethodSignatureValidator
{
    public static readonly System.Collections.Generic.Dictionary<string, ulong> ServerSignatures = new()
    {
        { "ICounterService.Add", 0xC40DDF36C397AD30UL },
        { "ICounterService.Reset", 0x5193617159A869E0UL },
    };

    public static System.Collections.Generic.List<string>? ValidateClientSignatures(
        System.Collections.Generic.Dictionary<string, ulong> clientSignatures)
    {
        var mismatches = new System.Collections.Generic.List<string>();

        foreach (var (methodKey, clientHash) in clientSignatures)
        {
            if (ServerSignatures.TryGetValue(methodKey, out var serverHash))
            {
                if (clientHash != serverHash)
                {
                    mismatches.Add($"{methodKey}: signature mismatch");
                }
            }
            // Skip unknown methods — test validator doesn't track all generated method signatures
        }

        return mismatches.Count > 0 ? mismatches : null;
    }
}

/// <summary>
/// Generated entity grain resolver for test state types.
/// Resolves state type names to IEntityGrain references using compile-time switch.
/// </summary>
public sealed class GeneratedEntityGrainResolver : IEntityGrainResolver
{
    public IEntityGrainBase? GetEntityGrain(IGrainFactory grainFactory, string stateTypeName, string entityId)
    {
        return stateTypeName switch
        {
            "CounterState" or "SharedMeta.Test.Meta1.CounterState"
                => grainFactory.GetGrain<IEntityGrain<CounterState>>(entityId),
            _ => null
        };
    }

    public IEntityGrainBase? GetEntityGrainByService(IGrainFactory grainFactory, string serviceName, string entityId)
    {
        return serviceName switch
        {
            "ICounterService" => grainFactory.GetGrain<IEntityGrain<CounterState>>(entityId),
            _ => null
        };
    }
}
