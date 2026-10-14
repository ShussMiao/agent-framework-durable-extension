// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Agents.AI.DurableTask;

internal static class DurableAgentDeploymentConfiguration
{
    internal const string EnvironmentVariableName = "DURABLE_AGENTS_DEPLOYMENT_MODE";
    internal const string IsolatedV2EnvironmentValue = "isolated_v2";

    internal static DurableAgentDeploymentMode Resolve(
        DurableAgentDeploymentMode? configuredMode,
        string? environmentValue)
    {
        if (environmentValue is not null &&
            !string.Equals(environmentValue, IsolatedV2EnvironmentValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DURABLE_AGENTS_DEPLOYMENT_MODE must be unset or exactly 'isolated_v2'; " +
                "empty values, aliases, case changes and surrounding whitespace are not supported.");
        }

        if (environmentValue is not null && configuredMode == DurableAgentDeploymentMode.Legacy)
        {
            throw new InvalidOperationException(
                "Explicit Legacy deployment mode conflicts with DURABLE_AGENTS_DEPLOYMENT_MODE=isolated_v2. " +
                "Remove the environment setting or explicitly acknowledge IsolatedV2 in code.");
        }

        return configuredMode ?? (environmentValue is null
            ? DurableAgentDeploymentMode.Legacy
            : DurableAgentDeploymentMode.IsolatedV2);
    }
}
