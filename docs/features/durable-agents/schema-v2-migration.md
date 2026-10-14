# Moving durable agents to schema v2

This guide is for application owners moving C# or Python durable agents to the canonical schema `2.0.0` state contract. The safe default is **read compatible state first, authorize a new writer separately, and keep old work on its original deployment**.

Use versions documented as schema-v2-compatible for your host, workers, clients, readers and recovery tools. A package's version number, successful deserialization, a new hub name or an environment variable alone does not establish that compatibility.

## Which runtime are you deploying?

| Runtime capability | No deployment acknowledgement | Explicit isolated v2 | Existing legacy state |
| --- | --- | --- | --- |
| C# with `DurableAgentsOptions.DeploymentMode` | Legacy writer; new state remains 1.2, KeepAll is the retention default | New canonical 2.0 state with either KeepAll or Auto | Readable; isolated writers reject new turns without changing the old state |
| Current Python canonical runtime requiring `isolated_v2` | Host construction rejects; there is no Legacy mode or default 1.2 writer in this runtime | New canonical 2.0 state, with retention/budget configured independently | Detached readers accept supported 1.x; activation does not convert it |
| An existing Python legacy deployment | Continue its existing compatible older package and configuration | Do not reinterpret the old deployment as a canonical writer | Keep sessions and in-flight workflows on that deployment |
| A later Python version explicitly documenting reader-first Legacy defaults | Follow that version's documented defaults and code/environment resolution | Acknowledgement remains separate from retention | Its documented legacy preservation rules must still be satisfied |

**Reader-first Legacy defaults are a joint target only for compatible versions that explicitly support them.** The current Python canonical runtime does not yet implement those defaults. The C# option described here does not change Python's runtime. Do not use an unspecified Python `deployment_mode` as a legacy-writer example.
Older Python legacy producers can create schema 1.1 state; do not equate all older Python deployments with C#'s schema-1.2 default.

Python's canonical mutable `DurableAgentState` is provided by `_shared_agent_state`; `_durable_agent_state` is the legacy-only implementation. `read_agent_state` is a detached version-aware reader, not permission to resume a session on another runtime. Compatible JSON does not imply compatible provider sessions or workflow replay.

## Decide before changing configuration

1. **Must existing sessions or workflows continue?** Keep their original compatible workers, hub and routing available. Drain or fence admission as needed. Do not move recorded workflow histories to a new engine.
2. **Do you need only to inspect or poll state?** Deploy compatible readers without granting schema-2 writer authority.
3. **Are genuinely new sessions ready for v2?** Create a fresh isolated hub, compatible fleet and new session identities, then acknowledge the deployment.
4. **Do you also want transcript pressure eviction?** Configure it separately. Schema v2 does not require eviction.
5. **Does another system own history?** Preserve its data and continuation. Neither local retention nor a new hub migrates that system.

## Prepare the fleet and preserve old work

- Inventory every worker, application client, poller, importer, scheduler, dashboard and rollback binary that can reach the namespace. Verify each understands the mailbox, owner binding, continuation and runtime profiles it will encounter, or rejects v2 before mutation and cannot be routed to it.
- Export canonical old entity state and required provider data/continuations using your application's security policy. Verify recovery preserves identity and original completion evidence without replaying side effects.
- Preserve queued and in-flight correlations, delayed signals and orchestration histories on the original deployment. A new correlation or identity is not a safe retry of old work.
- Use a new hub with compatible workers only. New identities within an existing hub are acceptable only with independently enforced routing segregation. Start fresh workflows when the selected runtime requires it, including changes between earlier v2 workflow engines.
- Keep original deployments and backups until their delivery, recovery and retention obligations are satisfied. An application-curated context seed in a new session is new work, not a migrated completion ledger.

## Acknowledge isolation separately from retention

The shared environment setting is:

```text
DURABLE_AGENTS_DEPLOYMENT_MODE=isolated_v2
```

This is an **operator acknowledgement**, not a test of peer workers, routing, client versions, hub contents or physical isolation.

### C#: exact code/environment rules

`DeploymentMode` is `Legacy` unless code or the exact environment value acknowledges v2.

| Code | Environment | Resolved deployment |
| --- | --- | --- |
| Unspecified | Unset | Legacy |
| Unspecified | `isolated_v2` | IsolatedV2 |
| Explicit Legacy | Unset | Legacy |
| Explicit IsolatedV2 | Unset or `isolated_v2` | IsolatedV2 |
| Explicit Legacy | `isolated_v2` | Reject conflicting configuration |
| Any | Present empty, another value, different case or surrounding whitespace | Reject configuration |

There is no `legacy` environment alias. Values are not trimmed or case-normalized. Undefined C# enum values are rejected. Invalid deployment, retention or budget combinations fail before factory invocation or worker/client transport-registration callbacks.

The environment is captured once when the completed host configuration is validated. Composed configuration calls validate their code settings against the same captured environment. Property setters do not read it; after validation the getter reports the resolved mode. Changing the process environment later does not flip an executing worker's state writer. To change deployment settings, stop admission and configure a new host deliberately.

### Python: check the contract of the version you use

The current canonical Python hosts accept only exact `deployment_mode="isolated_v2"` or the environment value above. Their current parser uses the environment only when the argument is omitted; an explicit argument takes precedence. Do not assume C#'s stricter conflict detection exists in that Python version. Configure agreeing code/environment values and verify them before deployment.

Reader-first Python versions may subsequently document aligned conflict handling, snapshots and Legacy defaults. Use those rules only once that capability is actually available in your chosen compatible version.

## Choose retention without changing the state schema

### C#: Legacy default and isolated KeepAll

```csharp
// Environment must be unset to keep this deployment explicitly legacy.
services.ConfigureDurableAgents(options =>
{
    options.DeploymentMode = DurableAgentDeploymentMode.Legacy;
    options.HistoryRetentionMode = DurableAgentHistoryRetentionMode.KeepAll;
    options.AddAIAgent(agent);
});

// A separate fresh deployment: schema 2, without proactive transcript pruning.
services.ConfigureDurableAgents(options =>
{
    options.DeploymentMode = DurableAgentDeploymentMode.IsolatedV2;
    options.HistoryRetentionMode = DurableAgentHistoryRetentionMode.KeepAll;
    options.AddAIAgent(agent);
});
```

These are alternatives for separately configured deployments, not successive calls switching a live host. You may omit the IsolatedV2 code assignment when the agreeing environment setting supplies it.

### C#: isolated automatic pressure retention

```csharp
services.ConfigureDurableAgents(options =>
{
    options.DeploymentMode = DurableAgentDeploymentMode.IsolatedV2;
    options.HistoryRetentionMode = DurableAgentHistoryRetentionMode.Auto;
    // Default is 1 MiB; optionally override for a measured deployment:
    // options.MaxStateBytes = 32 * 1024;
    options.HistoryRetentionHighWatermark = 0.85;
    options.HistoryRetentionLowWatermark = 0.70;
    options.AddAIAgent(agent);
});
```

Auto without IsolatedV2 is invalid. Its default budget is exactly 1,048,576 bytes (1 MiB); a positive
`MaxStateBytes` overrides it. Explicit null also uses that default and never disables Auto. Watermarks
default to high .85 / low .70, with `0 < low < high <= 1` under Auto.

C# KeepAll makes budget/watermark settings behaviorally inert, even when overridden; they do not trigger
pressure eviction or require clearing to switch from Auto. Property setters retain positive-or-null budget
and finite (0, 1] watermark domains, but reversed/equal watermark relationships are ignored under KeepAll.
Backend/provider limits still apply. Both policies use the same canonical mailbox and fixed-owner contracts.

### Python: isolated KeepAll without pressure, or with pressure

```python
from agent_framework_durabletask import DurableAIAgentWorker

# Current canonical runtime: acknowledgement is mandatory even for keep_all.
agents = DurableAIAgentWorker(
    worker,
    deployment_mode="isolated_v2",
    retention="keep_all",
    max_state_bytes=None,
)
agents.add_agent(agent)

# Alternative deployment: pressure eviction without eager compaction pruning.
agents = DurableAIAgentWorker(
    worker,
    deployment_mode="isolated_v2",
    retention="keep_all",
    max_state_bytes=32 * 1024,
    high_watermark=0.85,
    low_watermark=0.70,
)
```

For Functions, use the corresponding `AgentFunctionApp(deployment_mode="isolated_v2", retention="keep_all", max_state_bytes=...)`.

Python's byte budget is independent of `keep_all` versus `follow_compaction`: a positive budget can evict eligible history even with `keep_all`; `follow_compaction` separately opts into removal of eligible compaction exclusions. Explicit `None` disables pressure eviction, not backend limits. Standalone `DurableTaskSchedulerWorker` budget omission inherits the known 1 MiB budget even with `keep_all`; other standalone backends and Functions default to `None` rather than inferring that limit. Functions rejects `"backend_limit"`. C# differs: Auto has its own 1 MiB fallback, explicit null does not disable it, and KeepAll ignores budgets. No Python budget-default change is implied by the C# feature.

### External or service-owned history

For C#, register a stable non-secret logical provider key through the existing history configuration:

```csharp
options.AddAIAgent(externalOrServiceAgent, timeToLive: null,
    configureHistory: history =>
        history.ProviderKey = new DurableAgentHistoryProviderKey("application.history.v1"));
```

For Python, register the agent with its supported external provider or service-managed conversation configuration as documented by that provider; the worker budget does not give permission to delete the provider's data.

Both local retention policies preserve binding and continuation and never trim the external store or model-service history. Do not switch owners, clear continuation or create a replacement remote conversation to make state fit. Load/store failures may be durably classified by a runtime's supported terminal-failure contract; this does not prove the external system had no effects.

## What survives pruning, expiry and restart?

- **Transcript** supplies model context only when the entity owns history. Pressure retention removes eligible old atomic groups, not half of a correlated exchange or tool-call/result chain.
- **Compaction** controls what the model sees. It is not automatically durable-store deletion. C# Auto is not FollowCompaction; Python's eager compaction policy is a separate opt-in.
- **Mailbox results** retain caller-visible responses/failures for the configured delivery window. Physical cleanup may lag logical expiry.
- **Completion receipts** remain after result expiry and transcript eviction. An expired result is unavailable, not permission to rerun the correlation.
- **Protected state** includes receipts, retained terminal results, owner binding, provider/agent continuation, execution metadata, opaque fields and connected newest/system/tool components. A protected floor can exhaust the budget without any safely evictable data.
- **Capacity errors** fail rather than deleting evidence. Deterministically knowable floors can reject before callbacks; large new responses or external continuations can fail afterward. External model/tool/provider effects are not rolled back.
- **Entity TTL** deletes a session and its deduplication evidence. C# Legacy retains its historical TTL; public C# schema-2 idle deletion is disabled. Result cleanup does not delete receipts. Treat any explicit reset supported by a runtime as destructive—not routine retention.
- **Restart** reloads binding, continuation and receipts. JSON compatibility alone does not authorize another language, provider pipeline or workflow engine to continue the session.

An isolated C# worker rejects loaded 1.0/1.1/1.2 state, even an empty-looking snapshot. Cleanup must preserve it, not erase it and authorize the same identity as fresh. Only actual SDK absence can initialize a new generation. Do not change schema headers, manufacture empty receipt maps or infer complete history from absent truncation metadata.

Where a Python version offers a separate privileged migration API, it is not automatic activation: use that API's complete evidence, ownership-transfer and separate-empty-destination requirements. There is no public C# in-place migration callback. If authoritative evidence is unavailable, preserve the old deployment and start genuinely new work separately.

## Validation and monitoring checklist

- [ ] All reachable readers and writers are documented compatible and correctly routed.
- [ ] Old instances, correlation IDs, provider data and recovery binaries remain available and fenced.
- [ ] New hub/session/workflow identities and new remote conversations, where needed, are ready.
- [ ] Code/environment values agree and pass exact validation.
- [ ] Retention and measured budgets account for full state, transport limits and protected growth.
- [ ] An isolated test verifies restart, old-correlation duplicate delivery, expiry, state/outgoing-signal atomicity and failure rollback on the actual intended backend.
- [ ] Later entity-owned model input excludes evicted transcript while retained results/receipts remain authoritative.
- [ ] Logs distinguish configuration rejection, incompatible state/owner, unavailable results and capacity failures.
- [ ] Retention telemetry is treated as attempt-level evidence, not an exactly-once committed-state ledger.

Use [C# Sample 10](../../../dotnet/samples/DurableAgents/ConsoleApps/10_AutoHistoryRetention/README.md) or the corresponding Python examples as bounded demonstrations, not proof that an arbitrary fleet or payload fits. Receipt growth remains unbounded; no retention policy promises unlimited turns.

## If activation is rejected

Stop or keep admission fenced. Correct the code/environment conflict or invalid budget before configuring a new host. If the entity is legacy, preserve its exact state and original deployment; create a genuinely new isolated session rather than clearing it or retrying old work under a different correlation. If ownership/continuation is incompatible, restore the matching provider configuration or recover separately. If the protected floor is too large, choose a safe measured budget or application-level payload/reference strategy—not evidence deletion.

## Rollback without losing evidence

1. Stop new admission; drain or preserve in-flight work. Export canonical state and required provider continuation/data.
2. Roll back only to versions documented compatible with the actual mailbox, ownership, expiry and workflow contracts. Do not send v2 sessions to a transcript-only writer.
3. In C#, retain IsolatedV2 and select KeepAll to stop proactive pruning while continuing canonical writes. Returning to Legacy permits read-only committed v2 duplicates/polling but rejects new v2 work and mutating result cleanup; it does not downgrade state.
4. To restore a legacy-only application, use its preserved original sessions/deployment or a suitable pre-activation backup in a separately isolated namespace. Never rewrite a v2 header or drop receipts.
5. Already-pruned context cannot be recovered from expired results. Replaying work may repeat external effects. Application-level recovery must account for those effects; no lossless in-place downgrade is promised.

A later release **may** make v2 the default for genuinely new state as a deliberate compatibility milestone. Workers, clients, readers and rollback tools would need preparation, and treatment of existing state would have to be explicit. This is not a date, release commitment, silent migration promise or permission to skip today's acknowledgement.
