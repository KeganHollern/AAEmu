#nullable enable

using System.Runtime.CompilerServices;
using System.Text.Json;

using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace AAEmu.UnitTests;

[NotInParallel]
public class NLogConfigurationTests
{
    private const string MinimumLevelEnvironmentVariable = "AAEMU_LOG_LEVEL";

    public static IEnumerable<(string ConfigurationPath, string ServiceName)> ConfigurationPaths()
    {
        yield return (GetConfigurationPath("AAEmu.Game"), "aaemu-game");
    }

    [Test]
    [MethodDataSource(nameof(ConfigurationPaths))]
    public async Task Configuration_DefaultsToInfoAndRoutesJsonToStdoutOnly(string configurationPath, string serviceName)
    {
        using var environment = new EnvironmentVariableScope(MinimumLevelEnvironmentVariable, null);
        using var logFactory = new LogFactory();
        var configuration = new XmlLoggingConfiguration(configurationPath, logFactory);

        await Assert.That(configuration.AllTargets).HasSingleItem();
        await Assert.That(configuration.LoggingRules.Count).IsGreaterThanOrEqualTo(1);

        var configuredTarget = configuration.AllTargets.Single();
        await Assert.That(configuredTarget).IsTypeOf<AsyncTargetWrapper>();

        var asyncTarget = (AsyncTargetWrapper)configuredTarget;
        await Assert.That(asyncTarget.WrappedTarget).IsTypeOf<ConsoleTarget>();

        var stdoutTarget = (ConsoleTarget)asyncTarget.WrappedTarget!;
        await Assert.That(stdoutTarget.StdErr.RenderValue(LogEventInfo.CreateNullEvent(), false)).IsFalse();
        await Assert.That(stdoutTarget.Layout).IsTypeOf<JsonLayout>();

        var rule = configuration.LoggingRules.Single(loggingRule => loggingRule.LoggerNamePattern == "*");
        await Assert.That(rule.Targets).HasSingleItem();
        await Assert.That(ReferenceEquals(rule.Targets.Single(), asyncTarget)).IsTrue();
        await Assert.That(rule.IsLoggingEnabledForLevel(LogLevel.Trace)).IsFalse();
        await Assert.That(rule.IsLoggingEnabledForLevel(LogLevel.Debug)).IsFalse();
        await Assert.That(rule.IsLoggingEnabledForLevel(LogLevel.Info)).IsTrue();
        await Assert.That(rule.IsLoggingEnabledForLevel(LogLevel.Fatal)).IsTrue();

        var jsonLayout = (JsonLayout)stdoutTarget.Layout;
        await Assert.That(jsonLayout.IncludeEventProperties).IsTrue();
        await Assert.That(jsonLayout.IncludeScopeProperties).IsTrue();

        var logEvent = new LogEventInfo(LogLevel.Info, "AAEmu.Test", "one line");
        logEvent.Properties["eventProperty"] = "event-value";
        string renderedEvent;
        using (ScopeContext.PushProperty("scopeProperty", "scope-value"))
            renderedEvent = jsonLayout.Render(logEvent);
        using var json = JsonDocument.Parse(renderedEvent);

        await Assert.That(renderedEvent.Contains('\n')).IsFalse();
        await Assert.That(renderedEvent.Contains('\r')).IsFalse();
        await Assert.That(json.RootElement.GetProperty("service").GetString()).IsEqualTo(serviceName);
        await Assert.That(json.RootElement.GetProperty("level").GetString()).IsEqualTo("INFO");
        await Assert.That(json.RootElement.GetProperty("logger").GetString()).IsEqualTo("AAEmu.Test");
        await Assert.That(json.RootElement.GetProperty("message").GetString()).IsEqualTo("one line");
        await Assert.That(json.RootElement.GetProperty("eventProperty").GetString()).IsEqualTo("event-value");
        await Assert.That(json.RootElement.GetProperty("scopeProperty").GetString()).IsEqualTo("scope-value");
    }

    [Test]
    [MethodDataSource(nameof(ConfigurationPaths))]
    public async Task Configuration_AaemuLogLevelOverridesMinimumLevel(string configurationPath, string _)
    {
        using var environment = new EnvironmentVariableScope(MinimumLevelEnvironmentVariable, "Debug");
        using var logFactory = new LogFactory();
        var configuration = new XmlLoggingConfiguration(configurationPath, logFactory);
        var rule = configuration.LoggingRules.Single(loggingRule => loggingRule.LoggerNamePattern == "*");

        await Assert.That(rule.IsLoggingEnabledForLevel(LogLevel.Trace)).IsFalse();
        await Assert.That(rule.IsLoggingEnabledForLevel(LogLevel.Debug)).IsTrue();
        await Assert.That(rule.IsLoggingEnabledForLevel(LogLevel.Info)).IsTrue();
    }

    private static string GetConfigurationPath(string projectName, [CallerFilePath] string sourceFilePath = "")
    {
        var repositoryPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, ".."));
        return Path.Combine(repositoryPath, projectName, "NLog.config");
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
        }
    }
}
