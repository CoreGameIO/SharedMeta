// UNIT-TEST: the case under test is a build where no service carries [MetaServiceImpl(DeepDesync = true)],
// which the test cluster cannot represent - its services do carry the attribute, and the count the check
// receives is baked in by the generator at compile time.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Grains;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Startup reporting for the one deep desync configuration that cannot work: the runtime switch is on
/// while the compile-time attribute is nowhere in the build, so no comparison is ever generated.
/// </summary>
public class DeepDesyncConfigurationCheckTests
{
    [Fact]
    public async Task SwitchOn_NoServiceCarriesTheAttribute_ReportsThatNothingWillEverBeCompared()
    {
        var logger = new RecordingLogger();

        await StartCheckAsync(servicesWithDeepDesync: 0, deepDesyncEnabled: true, logger);

        var error = Assert.Single(logger.Errors);
        Assert.Contains("no service in this build declares", error);
    }

    [Fact]
    public async Task SwitchOn_SomeServiceCarriesTheAttribute_SaysNothing()
    {
        var logger = new RecordingLogger();

        await StartCheckAsync(servicesWithDeepDesync: 1, deepDesyncEnabled: true, logger);

        Assert.Empty(logger.Errors);
    }

    [Fact]
    public async Task SwitchOff_NoServiceCarriesTheAttribute_SaysNothing()
    {
        var logger = new RecordingLogger();

        await StartCheckAsync(servicesWithDeepDesync: 0, deepDesyncEnabled: false, logger);

        Assert.Empty(logger.Errors);
    }

    [Fact]
    public async Task SwitchLeftAtDefault_NoServiceCarriesTheAttribute_SaysNothing()
    {
        var logger = new RecordingLogger();

        await StartCheckAsync(servicesWithDeepDesync: 0, deepDesyncEnabled: null, logger);

        Assert.Empty(logger.Errors);
    }

    private static Task StartCheckAsync(int servicesWithDeepDesync, bool? deepDesyncEnabled, RecordingLogger logger)
    {
        var options = Options.Create(new EntityGrainOptions { DeepDesyncEnabled = deepDesyncEnabled });
        var check = new DeepDesyncConfigurationCheck(servicesWithDeepDesync, options, logger);

        return check.StartAsync(CancellationToken.None);
    }

    private sealed class RecordingLogger : ILogger<DeepDesyncConfigurationCheck>
    {
        public List<string> Errors { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Error) return;

            Errors.Add(formatter(state, exception));
        }
    }
}
