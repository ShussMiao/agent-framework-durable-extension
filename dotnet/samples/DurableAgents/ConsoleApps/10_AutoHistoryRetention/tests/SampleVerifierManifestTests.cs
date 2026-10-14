// Copyright (c) Microsoft. All rights reserved.

using VerifySamples;

namespace AutoHistoryRetentionTests;

public sealed class SampleVerifierManifestTests
{
    [Fact]
    public void ManifestAcceptsDocumentedEnvironmentWithoutObsoleteAcknowledgement()
    {
        SampleDefinition sample = GetSample();
        Dictionary<string, string> environment = CreateDocumentedEnvironment();

        Assert.Null(sample.SkipReason);
        Assert.Empty(sample.OptionalEnvironmentVariables);
        Assert.Equal(
            environment.Keys.Order(StringComparer.Ordinal),
            sample.RequiredEnvironmentVariables.Order(StringComparer.Ordinal));
        Assert.Empty(GetMissingRequirements(sample, environment));
    }

    [Theory]
    [InlineData("FOUNDRY_PROJECT_ENDPOINT")]
    [InlineData("FOUNDRY_MODEL")]
    [InlineData("DURABLE_TASK_SCHEDULER_CONNECTION_STRING")]
    [InlineData("DURABLE_AGENTS_DEPLOYMENT_MODE")]
    public void ManifestRequiresEachDocumentedVariable(string missingVariable)
    {
        Dictionary<string, string> environment = CreateDocumentedEnvironment();
        environment.Remove(missingVariable);

        Assert.Equal(missingVariable, Assert.Single(GetMissingRequirements(GetSample(), environment)));
    }

    private static SampleDefinition GetSample()
        => Assert.Single(
            DurableAgentSamples.ConsoleApps,
            sample => sample.Name == "DurableAgents_Console_10_AutoHistoryRetention");

    private static Dictionary<string, string> CreateDocumentedEnvironment()
        => new(StringComparer.Ordinal)
        {
            ["FOUNDRY_PROJECT_ENDPOINT"] = "https://example.test/api/projects/sample",
            ["FOUNDRY_MODEL"] = "sample-model",
            ["DURABLE_TASK_SCHEDULER_CONNECTION_STRING"] =
                "Endpoint=http://localhost:8080;TaskHub=retention-isolated-demo;Authentication=None",
            ["DURABLE_AGENTS_DEPLOYMENT_MODE"] = "isolated_v2",
        };

    private static IEnumerable<string> GetMissingRequirements(
        SampleDefinition sample,
        Dictionary<string, string> environment)
        => sample.RequiredEnvironmentVariables.Where(
            variable => !environment.TryGetValue(variable, out string? value) || string.IsNullOrEmpty(value));
}
