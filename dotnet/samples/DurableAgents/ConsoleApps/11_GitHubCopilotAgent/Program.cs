// Copyright (c) Microsoft. All rights reserved.

using GitHub.Copilot;
using GitHubCopilotDurableAgent;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

string dtsConnectionString = Environment.GetEnvironmentVariable("DURABLE_TASK_SCHEDULER_CONNECTION_STRING")
    ?? "Endpoint=http://localhost:8080;TaskHub=default;Authentication=None";
string? copilotRuntimeUrl = Environment.GetEnvironmentVariable("COPILOT_RUNTIME_URL");
string? copilotBaseDirectory = Environment.GetEnvironmentVariable("COPILOT_BASE_DIRECTORY");

Console.WriteLine("=== Durable GitHub Copilot Agent Sample ===");
Console.WriteLine($"Agent Name: {GitHubCopilotDurableDemo.AgentName}");
Console.WriteLine($"History ReplayMode: {DurableAgentHistoryReplayMode.CurrentRequestOnly}");
Console.WriteLine($"History ProviderKey: {GitHubCopilotDurableDemo.HistoryProviderKey}");
Console.WriteLine($"Copilot Connection: {(string.IsNullOrWhiteSpace(copilotRuntimeUrl) ? "Local Copilot CLI" : copilotRuntimeUrl)}");
Console.WriteLine();

await using CopilotClient copilotClient = new(
    GitHubCopilotDurableDemo.CreateClientOptions(copilotRuntimeUrl, copilotBaseDirectory));

string instanceId;

// ---------------------------------------------------------------------------
// Host 1: Start the incident remediation orchestration, run Turn 1 on
//         CopilotAgent to propose a plan, then shut down Host 1 completely
//         while the orchestration is dehydrated waiting for human approval.
// ---------------------------------------------------------------------------
IHost firstHost = CreateHost(copilotClient, dtsConnectionString);
await firstHost.StartAsync();
try
{
    DurableTaskClient firstHostClient = firstHost.Services.GetRequiredService<DurableTaskClient>();

    CopilotIncidentInput incidentInput = new(
        IncidentSummary: "Elevated HTTP 503 errors on checkout-service after deployment v2.4.1",
        ApprovalTimeoutMinutes: 30);

    instanceId = await firstHostClient.ScheduleNewOrchestrationInstanceAsync(
        nameof(GitHubCopilotDurableDemo.RunIncidentOrchestrationAsync),
        incidentInput);
    Console.WriteLine($"[Host 1] Started orchestration '{instanceId}'. Waiting for Turn 1 remediation plan...");

    while (true)
    {
        OrchestrationMetadata? metadata = await firstHostClient.GetInstanceAsync(instanceId, getInputsAndOutputs: true);
        string? customStatus = metadata?.ReadCustomStatusAs<string>();
        if (customStatus?.Contains(GitHubCopilotDurableDemo.ApprovalEventName, StringComparison.Ordinal) is true)
        {
            Console.WriteLine($"[Host 1] {customStatus}");
            break;
        }

        if (metadata?.RuntimeStatus is OrchestrationRuntimeStatus.Completed
            or OrchestrationRuntimeStatus.Failed
            or OrchestrationRuntimeStatus.Terminated)
        {
            break;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(250));
    }
}
finally
{
    await StopAndDisposeHostAsync(firstHost);
}

Console.WriteLine();
Console.WriteLine("[Host 1] Stopped and disposed completely. Zero worker processes are running while awaiting operator approval.");
Console.Write("Approve the proposed remediation plan? [y/n] (default: y): ");
string? approvalInput = Console.ReadLine();
bool approved = string.IsNullOrWhiteSpace(approvalInput) ||
    approvalInput.Trim().Equals("y", StringComparison.OrdinalIgnoreCase);

// ---------------------------------------------------------------------------
// Host 2: Start a brand-new worker host after the operator responds, raise the
//         external approval event, replay the orchestration, cancel the SLA
//         timer, and resume the exact same GitHubCopilotAgentSession.SessionId
//         for Turn 2.
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("[Host 2] Starting new worker host to deliver approval event and resume Copilot session for Turn 2...");
IHost secondHost = CreateHost(copilotClient, dtsConnectionString);
await secondHost.StartAsync();
try
{
    DurableTaskClient secondHostClient = secondHost.Services.GetRequiredService<DurableTaskClient>();

    await secondHostClient.RaiseEventAsync(
        instanceId,
        GitHubCopilotDurableDemo.ApprovalEventName,
        approved);

    OrchestrationMetadata completed = await secondHostClient.WaitForInstanceCompletionAsync(
        instanceId,
        getInputsAndOutputs: true);

    CopilotIncidentResult? result = completed.ReadOutputAs<CopilotIncidentResult>();
    Console.WriteLine($"[Host 2] Orchestration completed with status: {completed.RuntimeStatus}");
    Console.WriteLine($"Turn 1 Proposed Plan: {result?.ProposedPlan}");
    Console.WriteLine($"Approved: {result?.Approved} (TimedOut: {result?.TimedOut})");
    Console.WriteLine($"Turn 2 Final Resolution: {result?.FinalResolution}");
}
finally
{
    await StopAndDisposeHostAsync(secondHost);
}

static IHost CreateHost(CopilotClient copilotClient, string dtsConnectionString)
{
    AIAgent copilotAgent = GitHubCopilotDurableDemo.CreateCopilotAgent(copilotClient);

    return Host.CreateDefaultBuilder()
        .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
        .ConfigureServices(services =>
        {
            services.ConfigureDurableAgents(
                options => GitHubCopilotDurableDemo.RegisterCopilotAgent(options, copilotAgent),
                workerBuilder: builder =>
                {
                    builder.UseDurableTaskScheduler(dtsConnectionString);
                    builder.AddTasks(registry =>
                    {
                        registry.AddOrchestratorFunc<CopilotIncidentInput, CopilotIncidentResult>(
                            nameof(GitHubCopilotDurableDemo.RunIncidentOrchestrationAsync),
                            GitHubCopilotDurableDemo.RunIncidentOrchestrationAsync);
                    });
                },
                clientBuilder: builder => builder.UseDurableTaskScheduler(dtsConnectionString));
        })
        .Build();
}

static async Task StopAndDisposeHostAsync(IHost host)
{
    try
    {
        await host.StopAsync();
    }
    finally
    {
        host.Dispose();
    }
}
