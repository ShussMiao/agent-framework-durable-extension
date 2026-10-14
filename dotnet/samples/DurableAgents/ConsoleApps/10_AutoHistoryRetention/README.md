# Opt-in Pressure Retention Sample

> [!IMPORTANT]
> This sample separately acknowledges IsolatedV2 and enables public Auto retention for a new session.
> Use versions documented as schema-v2-compatible in a fresh isolated hub with compatible workers,
> clients and readers. Existing 1.x sessions are rejected, not migrated. Follow the shared
> [C# and Python migration guide](../../../../../docs/features/durable-agents/schema-v2-migration.md).

The entry point uses the supported public options, a bounded model response, an illustrative marker
diagnostic and OpenTelemetry metrics. KeepAll remains the default outside this opt-in:

```csharp
services.ConfigureDurableAgents(options =>
{
    options.DeploymentMode = DurableAgentDeploymentMode.IsolatedV2;
    options.HistoryRetentionMode = DurableAgentHistoryRetentionMode.Auto;
    options.MaxStateBytes = 32 * 1024; // Demo override; Auto otherwise defaults to 1 MiB.
    options.HistoryRetentionHighWatermark = 0.85;
    options.HistoryRetentionLowWatermark = 0.70;
    options.AddAIAgent(agent);
});
```

## Scenario

The sample generates a random ASCII marker in its first project note, adds seven moderate turns
without printing their filler text, and asks the model to recall the exact marker after pressure
retention. Agent instructions keep every response under 20 words and `MaxOutputTokens = 64`
enforces a service-request bound so protected result mailboxes do not dominate the state floor.

The offline SDK tests check the 32 KiB budget against schema 2 state containing bounded terminal results,
completion receipts, the fixed history binding, serialized agent session, and bookkeeping.
Seven 4 KiB inputs plus their state envelopes cross the 85% high watermark, while the protected
mailbox and session floor still fits below it. `Auto` can therefore remove old model-transcript
groups and return state below the pressure threshold without deleting durable completion evidence.
The sample does not wait for wall-clock expiry and does not depend on a soft delivery window.

The random-marker answer is illustrative only:

- An exact marker match is classified as `Present`.
- Only `UNKNOWN` with optional final punctuation is classified as `Unavailable`.
- Every other response is `Inconclusive`.

Both the scenario helper and diagnostic classifier reject null, empty, or whitespace-only markers
with an argument exception. The scenario validates before invoking callbacks or the agent, so
invalid markers cannot cause model calls or mutate the session. Valid markers are preserved
unchanged and compared using exact, case-sensitive matching.

The real `Microsoft.Agents.AI.DurableTask.UnitTests` project provides white-box coverage through the
SDK transactional entity batch dispatcher. It captures later model input, inspects serialized and
reloaded state, retrieves the
original completed result from the mailbox, and redelivers the original correlation while checking
that the model is not executed again.

## What Auto retains

Pressure retention removes eligible model transcript. It does not remove authoritative terminal
results, completion receipts, the history-owner binding, serialized session continuation, or other
durable bookkeeping. Receipt-bearing mailbox entities are not TTL-deleted under the current public
policy. Entries connected by correlation and tool-call identity are treated as one atomic transcript
group, so retention does not leave half of an exchange behind.

This is durable entity **pressure retention**, not Microsoft Agent Framework stateful context
compaction or `FollowCompaction`. It does not summarize old messages. It removes old transcript
from later model input while durable execution and delivery evidence remains available.

Public Legacy and KeepAll remain the defaults. IsolatedV2 also supports KeepAll without proactive
transcript deletion. Auto requires isolated deployment acknowledgement and defaults to exactly 1 MiB;
this demonstration overrides it to 32 KiB. Explicit null uses the default; optional watermarks satisfy
`0 < low < high <= 1`. To stop proactive pruning, retain IsolatedV2 and choose KeepAll: existing budget
and watermark settings become inert without needing to clear them. Returning to Legacy does not downgrade schema-2 state or
authorize new work. No public in-place migration or receipt-deleting TTL is enabled.

`Auto` cannot make every payload fit. A single oversized protected newest inline `DataContent`,
tool result, terminal result, or other protected state can still exceed the high watermark. The
operation then fails rather than persisting oversized state. That protected-state failure is
different from cumulative transcript pressure, and application-level payload references or
transport limits remain separate concerns.

## OpenTelemetry metrics

The sample uses the standard OpenTelemetry metrics pipeline and console exporter:

```csharp
services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(DurableAgentTelemetry.MeterName)
        .AddConsoleExporter(...));
```

The shared meter is `agent_framework.durabletask`, exposed through
`DurableAgentTelemetry.MeterName`. Corrected retention builds emit the cross-runtime
`durable.retention.*` instruments:

- `durable.retention.evaluations`
- `durable.retention.budget`
- `durable.retention.state.size`
- `durable.retention.removed_messages`
- `durable.retention.removed_entries`
- `durable.retention.reclaimed_bytes`
- `durable.retention.capacity_failures`
- `durable.retention.write_attempts`
- `durable.retention.operations`

Retention dimensions are bounded operational fields such as `mechanism`, `outcome`,
`commit_status`, `phase`, `stage`, and `deletion_staged`. They do not include agent, session,
correlation, message, or content dimensions. Outcomes include `below_threshold`, `staged`,
`protected_floor`, `returned`, and `failed`.

These measurements are emitted for attempts before entity commit. A later persistence failure or
retry can roll back or duplicate what telemetry observed. Metrics are operational evidence, not
durable committed truth or an exact-once state query. Pair them with durable state and application
behavior when correctness matters.

## Run the sample

Install the .NET 10 SDK and configure Microsoft CFS for package restores. Create a fresh task hub with
only compatible workers; a non-default name and acknowledgement do not themselves certify isolation.
Set the model configuration in your own shell, using an identity authorized for that model:

```powershell
$env:FOUNDRY_PROJECT_ENDPOINT = "https://YOUR-ENDPOINT/api/projects/YOUR-PROJECT"
$env:FOUNDRY_MODEL = "YOUR-MODEL-DEPLOYMENT"
$env:DURABLE_TASK_SCHEDULER_CONNECTION_STRING = "Endpoint=http://localhost:8080;TaskHub=retention-isolated-demo;Authentication=None"
$env:DURABLE_AGENTS_DEPLOYMENT_MODE = "isolated_v2"
Set-Location dotnet\samples\DurableAgents\ConsoleApps\10_AutoHistoryRetention
dotnet restore --source https://packagefeedproxy.microsoft.io/nuget/v3/index.json
dotnet run --framework net10.0 --no-restore
```

Enter a project topic of at most 80 characters. The host creates a new durable session, sends seven
moderate notes and prints the diagnostic response with `Present`, `Unavailable` or `Inconclusive`.
No wall-clock expiry wait is required. The default provider owns the entity transcript; no old or remote
conversation is imported. `DefaultAzureCredential` is used for development; prefer a specific credential
for production.

Without the isolation acknowledgement, the entry point exits with code 2 before accessing model
configuration or services. Missing/default task-hub configuration fails explicitly with no fallback.
For an external NuGet consumer, replace the local product ProjectReference with a PackageReference to
the actual feature-introducing evaluation package/build; AF dependency versions alone do not supply Auto.
Backend/model execution requires your configured services; offline tests do not certify the hosted backend.

## Tests

```powershell
dotnet test --project tests\10_AutoHistoryRetention.Tests.csproj -c Release -f net10.0
```

The sample-local tests have no friend access and use only public APIs. They compile/configure Auto
without internal switches, reject invalid options before factory/transport callbacks, validate explicit
non-default hub selection, and cover bounded agent options, prompt construction,
the no-wait marker diagnostic, conservative result classification, invalid-marker rejection before
agent/model calls or output/session mutation, and real OpenTelemetry meter registration/export.
The offline verifier-manifest regression compiles the actual verifier definitions and checks the
four documented environment keys, including `DURABLE_AGENTS_DEPLOYMENT_MODE`, without the obsolete
acknowledgement. It does not run the verifier, sample host, backend or model.

The white-box retention regression lives in the real package unit-test project, which legitimately
has friend access:

```powershell
dotnet test --project ..\..\..\..\tests\Microsoft.Agents.AI.DurableTask.UnitTests\Microsoft.Agents.AI.DurableTask.UnitTests.csproj `
    -c Release -f net10.0 `
    --filter-class "*AgentEntityPublicRetentionTests"
```

Those regressions exercise public Auto without internal writer/migration authorization through the SDK
transactional dispatcher, including cold reload, mailbox/receipt preservation, duplicates, expiry,
legacy rejection, provider/service boundaries, protected capacity and commit/outbox rollback. Existing
history tests additionally cover connected tool/system grouping and attempt-level telemetry.
Validate actual isolated-backend atomicity before deploying to production.

## Retry safety with external history

The demo uses entity-owned history, but external providers and service-managed conversations remain
supported under isolated KeepAll and Auto with their existing logical-key and continuation requirements.
An external load/store callback failure propagates, rolls back local state/signals and creates no new
framework failed receipt. Load failure before model execution leaves the model uncalled; store failure
after execution can leave remote writes and model/tool effects ambiguous. The same uncommitted correlation
can run again. Cancellation, timeout and response loss can also be ambiguous.

Applications own idempotency and reconciliation. Preserve an application operation key, forward it to
tools/APIs/provider records, persist results and an ambiguity ledger, upsert deterministic message IDs and
look up remote status before retrying. Use a transactional outbox only inside an actual transaction;
manually reconcile non-idempotent effects when necessary. See
[public API examples](../../../../../docs/features/durable-agents/schema-v2-migration.md#make-retries-safe-in-your-application)
and [the provider retry-risk limitation](https://github.com/microsoft/agent-framework-durable-extension/issues/143).
