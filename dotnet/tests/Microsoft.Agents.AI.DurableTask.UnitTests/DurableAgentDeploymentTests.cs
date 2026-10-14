// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics;
using System.Text.Json;
using AutoHistoryRetention;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.AI.DurableTask.Tests.Unit;

public sealed class DurableAgentDeploymentTests
{
    private const string ProbeVariable = "DURABLE_AGENTS_DEPLOYMENT_TEST_PROBE";

    [Fact]
    public void SampleConfigurationUsesSchemaV2AndDemoRetentionBudget()
    {
        DurableAgentsOptions? configured = null;
        ServiceCollection services = new();
        services.ConfigureDurableAgents(options =>
        {
            HistoryRetentionDemo.ConfigureRetention(options);
            configured = options;
        });

        Assert.NotNull(configured);
        Assert.Equal(DurableAgentDeploymentMode.SchemaV2, configured.DeploymentMode);
        Assert.Equal(DurableAgentHistoryRetentionMode.Auto, configured.HistoryRetentionMode);
        Assert.Equal(HistoryRetentionDemo.MaxStateBytes, configured.MaxStateBytes);
        Assert.Equal(HistoryRetentionDemo.HighWatermark, configured.HistoryRetentionHighWatermark);
        Assert.Equal(HistoryRetentionDemo.LowWatermark, configured.HistoryRetentionLowWatermark);
    }

    [Fact]
    public void PublicDeploymentEnumHasOnlySchemaNamesAndUnchangedNumericValues()
    {
        Assert.Equal(["Legacy", "SchemaV2"], Enum.GetNames<DurableAgentDeploymentMode>());
        Assert.Equal(
            [DurableAgentDeploymentMode.Legacy, DurableAgentDeploymentMode.SchemaV2],
            Enum.GetValues<DurableAgentDeploymentMode>());
        Assert.Equal(0, (int)DurableAgentDeploymentMode.Legacy);
        Assert.Equal(1, (int)DurableAgentDeploymentMode.SchemaV2);
        Assert.Equal("1", JsonSerializer.Serialize(DurableAgentDeploymentMode.SchemaV2));
    }

    [Theory]
    [InlineData("unacknowledged")]
    [InlineData("invalidMode")]
    [InlineData("reversedWatermarks")]
    public void InvalidPublicSettingsRejectBeforeFactoryOrTransportCallbacks(string kind)
    {
        int callbacks = 0;
        ServiceCollection services = new();
        Exception? error = Record.Exception(() => services.ConfigureDurableAgents(
            options =>
            {
                if (kind != "unacknowledged")
                {
                    options.DeploymentMode = DurableAgentDeploymentMode.SchemaV2;
                }
                options.AddAIAgentFactory("agent", _ =>
                {
                    callbacks++;
                    throw new InvalidOperationException("Factory must not be invoked.");
                });
                options.HistoryRetentionMode = DurableAgentHistoryRetentionMode.Auto;
                if (kind == "invalidMode")
                {
                    options.HistoryRetentionMode = (DurableAgentHistoryRetentionMode)42;
                }
                if (kind == "reversedWatermarks")
                {
                    options.HistoryRetentionLowWatermark = 0.9;
                    options.HistoryRetentionHighWatermark = 0.8;
                }
            },
            workerBuilder: _ => callbacks++,
            clientBuilder: _ => callbacks++));

        if (kind == "invalidMode")
        {
            Assert.IsType<ArgumentOutOfRangeException>(error);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(error);
        }
        Assert.Equal(0, callbacks);
        if (kind == "unacknowledged")
        {
            Assert.Contains("DurableAgentsOptions.DeploymentMode", error!.Message, StringComparison.Ordinal);
            Assert.Contains("DurableAgentDeploymentMode.SchemaV2", error.Message, StringComparison.Ordinal);
            Assert.Contains("DURABLE_AGENTS_DEPLOYMENT_MODE=isolated_v2", error.Message, StringComparison.Ordinal);
        }
    }

    public static IEnumerable<object?[]> DeploymentCases()
    {
        foreach (int? code in new int?[] { null, 0, 1 })
        {
            foreach (string? environment in new string?[] { null, "isolated_v2", "", "legacy", "ISOLATED_V2", " isolated_v2", "isolated_v2 " })
            {
                foreach (bool auto in new[] { false, true })
                {
                    yield return [code, environment, auto];
                }
            }
        }
    }

    [Fact]
    public void UndefinedCodeModeRejectsWithoutReadingEnvironment()
    {
        DurableAgentsOptions options = new();
        Assert.Equal(DurableAgentDeploymentMode.Legacy, options.DeploymentMode);
        Assert.False(options.EnablePersistentRequestOutcomes);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.DeploymentMode = (DurableAgentDeploymentMode)42);
        Assert.Equal(DurableAgentDeploymentMode.Legacy, options.DeploymentMode);
    }

    [Theory]
    [MemberData(nameof(DeploymentCases))]
    public async Task PublicEnvironmentContractRunsInIsolatedChildProcessAsync(
        int? code, string? environment, bool auto)
    {
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(DurableAgentDeploymentTests).Assembly.Location);
        start.ArgumentList.Add("--filter-method");
        start.ArgumentList.Add("*DeploymentEnvironmentChildProbe");
        start.ArgumentList.Add("--no-progress");
        start.ArgumentList.Add("--show-live-output");
        start.ArgumentList.Add("on");
        start.Environment.Remove(DurableAgentDeploymentConfiguration.EnvironmentVariableName);
        if (environment is not null)
        {
            start.Environment[DurableAgentDeploymentConfiguration.EnvironmentVariableName] = environment;
        }
        start.Environment[ProbeVariable] = JsonSerializer.Serialize(new ProbeCase(code, environment, auto));
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Deployment probe did not start.");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        string output = await stdout;
        Assert.True(child.ExitCode == 0, output + (await stderr));
        Assert.Contains("DEPLOYMENT_PROBE_ASSERTIONS_COMPLETED", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentEnvironmentChildProbe()
    {
        string? serialized = Environment.GetEnvironmentVariable(ProbeVariable);
        if (serialized is null)
        {
            return;
        }
        ProbeCase test = JsonSerializer.Deserialize<ProbeCase>(serialized)!;
        bool invalidEnvironment = test.Environment is not null && test.Environment != "isolated_v2";
        bool conflicting = test.Code == 0 && test.Environment == "isolated_v2";
        bool isolated = test.Code == 1 || (test.Code is null && test.Environment == "isolated_v2");
        bool rejected = invalidEnvironment || conflicting || (test.Auto && !isolated);
        int callbacks = 0;
        DurableAgentsOptions? configured = null;
        ServiceCollection services = new();
        Exception? error = Record.Exception(() => services.ConfigureDurableAgents(
            options =>
            {
                configured = options;
                if (test.Code is int code)
                {
                    options.DeploymentMode = (DurableAgentDeploymentMode)code;
                }
                Assert.Equal(test.Code == 1 ? DurableAgentDeploymentMode.SchemaV2 : DurableAgentDeploymentMode.Legacy,
                    options.DeploymentMode);
                options.AddAIAgentFactory("probe", _ => throw new InvalidOperationException("Factory must not be invoked."));
                if (test.Auto)
                {
                    options.HistoryRetentionMode = DurableAgentHistoryRetentionMode.Auto;
                    options.MaxStateBytes = 32 * 1024;
                }
            },
            workerBuilder: _ => callbacks++,
            clientBuilder: _ => callbacks++));
        if (rejected)
        {
            Assert.IsType<InvalidOperationException>(error);
            Assert.Equal(0, callbacks);
            Console.WriteLine("DEPLOYMENT_PROBE_ASSERTIONS_COMPLETED");
            return;
        }

        Assert.Null(error);
        Assert.Equal(2, callbacks);
        Assert.NotNull(configured);
        Assert.Equal(isolated ? DurableAgentDeploymentMode.SchemaV2 : DurableAgentDeploymentMode.Legacy,
            configured.DeploymentMode);
        Assert.Equal(isolated, configured.EnablePersistentRequestOutcomes);

        // This process executes only this probe, so no parallel tests observe these environment changes.
        Environment.SetEnvironmentVariable(DurableAgentDeploymentConfiguration.EnvironmentVariableName, "invalid-after-snapshot");
        services.ConfigureDurableAgents(_ => { });
        Assert.Equal(isolated ? DurableAgentDeploymentMode.SchemaV2 : DurableAgentDeploymentMode.Legacy,
            configured.DeploymentMode);
        if (test.Environment == "isolated_v2")
        {
            InvalidOperationException conflict = Assert.Throws<InvalidOperationException>(() =>
                services.ConfigureDurableAgents(options => options.DeploymentMode = DurableAgentDeploymentMode.Legacy));
            Assert.Contains("conflicts", conflict.Message, StringComparison.Ordinal);
        }
        Console.WriteLine("DEPLOYMENT_PROBE_ASSERTIONS_COMPLETED");
    }

    private sealed record ProbeCase(int? Code, string? Environment, bool Auto);
}
