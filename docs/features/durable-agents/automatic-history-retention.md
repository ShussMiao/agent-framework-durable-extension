# Automatic durable-history retention in .NET

> [!IMPORTANT]
> This feature requires a build containing the public API described here and the independently approved provider-failure completion change in [#131](https://github.com/microsoft/agent-framework-durable-extension/pull/131). Source support is not a claim that a compatible package has been released. Deploy into a fresh isolated task hub, or new sessions with independently guaranteed compatible routing. Existing 1.x sessions are not converted by this API.

## Opt in explicitly

`KeepAll` remains the default: no proactive transcript deletion, new entities write schema `1.2.0`, and the historical idle TTL behavior is unchanged. Reader capability does not change that writer default. Configure Auto and its required positive byte budget together, before starting a worker:

```csharp
services.ConfigureDurableAgents(options =>
{
    options.HistoryRetentionMode = DurableAgentHistoryRetentionMode.Auto;
    options.MaxStateBytes = 32 * 1024;
    options.HistoryRetentionHighWatermark = 0.85; // optional; this is the default
    options.HistoryRetentionLowWatermark = 0.70;  // optional; this is the default
    options.AddAIAgent(agent);
});
```

The same `DurableAgentsOptions` surface is available through Azure Functions `ConfigureDurableAgents`. Invalid modes, nonpositive budgets, nonfinite watermarks, or watermarks outside `0 < low < high <= 1` fail clearly. A budget or watermark with KeepAll is invalid. Complete each configuration call before registration returns; partially configured Auto is not accepted.

Python does not have an equivalent public producer API in this feature. The source-level shared reader can inspect schema-2 results without enabling a mutable writer:

```python
from agent_framework_durabletask import read_agent_state

snapshot = read_agent_state(exported_canonical_json)
response = snapshot.try_get_agent_response(correlation_id)
```

That read-only snapshot is not permission to resume a .NET agent in Python or to route Python workers to this hub.

## What the budget means

Auto measures the complete UTF-8 escaped JSON-string entity storage envelope, not token count, raw JSON text length, or only `conversationHistory`. At the high watermark it evicts an oldest prefix of eligible atomic transcript components toward the low watermark. A write can succeed below high even when protected components prevent reaching low.

The newest exchange, system messages, and their connected correlation/tool-call/result closure are protected. So are terminal results, permanent completion receipts, owner binding, serialized agent/provider continuation, expiration/scheduling metadata, ingestion positions, and opaque/unknown JSON. Removed transcript is absent from subsequent entity-owned model input; result polling and old correlation redelivery still use the mailbox.

If protected state cannot fit below high, the operation fails with `DurableAgentStateSizeLimitExceededException` rather than trimming receipts or publishing oversized state. A deterministically knowable floor fails before model/provider calls. A large new response, tool result or continuation can fail afterward; external effects are not undone. Long tool or approval chains can connect much of the transcript into one protected component. Receipt growth is unbounded under the current policy, so Auto is not a promise of unlimited successful turns.

The sample's 32 KiB/.85/.70 profile with seven moderate inputs and a 64-token output bound is a bounded demonstration, not a universal production budget. Choose a measured budget below the actual backend write limit and keep individual requests/results within transport limits.

| Mechanism | Effect | Completion safety |
| --- | --- | --- |
| Model-context compaction | Reduces or summarizes model input; serialized compaction state still counts toward entity size | Does not delete durable transcript by itself |
| Auto transcript retention | Removes eligible entity-owned transcript | Never deletes mailbox results or receipts |
| Result payload expiry | `ResultRetentionPeriod`, default 60 seconds; physical cleanup can lag logical expiry | Preserves permanent succeeded/failed receipt; expired result is unavailable, not pending |
| Whole-entity TTL | Deletes the entire entity and its evidence | Legacy policy unchanged; receipt-bearing schema-2 deletion remains disabled |

FollowCompaction durable-store pruning is not implemented by this feature. Auto does not silently follow model compaction.

## History ownership

| Configuration | Auto behavior |
| --- | --- |
| Implicit default ChatClientAgent provider | Operation-scoped durable provider supplies the entity transcript; eligible old input is evicted |
| Generic agent with `PreloadEntityHistory` | Retained entity transcript is replayed, then current input; same eviction rules |
| Custom external provider | Stable `DurableAgentHistoryProviderKey` and usable declared continuation required; no transcript mirror or external deletion |
| Model-service conversation | Stable logical service key and compatible real restored conversation ID required; no remote-history deletion |
| Generic `CurrentRequestOnly`/opaque session | Explicit logical provider key required; session is protected, not trimmed |
| Explicit configured in-memory provider with reducers/filters | Rejected by the schema-2 fixed-owner contract; replacing it would silently change semantics |
| Local per-service-call history | Unsupported; public Agent Framework callbacks do not establish outer-run finality |

External providers remain responsible for their storage, consistency, retention, authorization and uncertain acknowledgements. Their continuation and all local delivery evidence still count against the entity budget. A provider load/store failure can become a sanitized durable failed result through #131, without claiming that no external effects occurred. Factory/session creation, cancellation, model/tool errors, serialization and capacity errors remain retryable unless a separately committed terminal contract says otherwise. Changed owners or missing continuation fail closed; Auto never resets an external conversation to make state fit.

## Compatibility and reader floor

Use the feature-introducing .NET evaluation build/release SHA, not a guessed released version. At main commit `95d82146a1f2f593563c8019d6019e0aa7e904c0`, the .NET converter and polling resolver already read schema `2.0.0` using receipts; new default writes remain 1.2. The pinned Agent Framework 1.23.0 and Durable Task SDK 1.18.0 dependencies alone do not supply this new public API.

At that same commit, Python `SharedAgentStateReader` in `_state_reader.py` validates detached canonical 2.0 snapshots and resolves results through receipts; its mutable `DurableAgentState` rejects shared 2.0 writes. This is a source-level read-only compatibility floor, not a claim about a released Python version or interchangeable session implementations. Verify and pin the actual package/build used by each consumer.

No compatible scheduler-dashboard release is certified here. Use verified mailbox-aware tools, or raw-JSON/read-only export tooling that retains the canonical `schemaVersion`/`data` root and opaque profiles. A transcript-entry count is not a completion count. Never project 2.0 into a legacy state root or interpret absent transcript results as permission to rerun.

Every reachable worker, poller, host, importer, scheduler, dashboard and rollback binary must implement the relevant mailbox/profile behavior or reject 2.0 before mutation and be isolated from its routing. Preserving unknown fields is insufficient. The options cannot inspect a distributed fleet or enforce a routing fence.

## Ordered upgrade and lossless isolation

1. Inventory task hubs, session identities, queued/inflight correlations, providers and all readers/writers/rollback binaries. Export or back up canonical old entity JSON and required external-store data/continuations under the application's security policy. Verify that restoring the export preserves identity and original result evidence; do not replay side effects just to test a backup.
2. Keep existing active sessions, old correlation IDs, orchestration history and delayed entity signals on the legacy deployment. Drain traffic as the application requires. Do not redeliver old work to a new identity or substitute a new correlation ID.
3. Deploy verified mailbox-aware readers first, including polling clients. Pin actual tested builds and isolate incompatible consumers. A skipped SDK version does not prove missing legacy completion history; repeat this inventory and reader check even when jumping directly to the feature release.
4. Provision a fresh isolated task hub with compatible workers only. New session identities within an existing hub are safe only if routing segregation is independently guaranteed. Create new durable sessions and new external/model conversations where applicable. Keep old state and its original deployment available; do not change its schema header.
5. Explicitly configure Auto with a positive measured budget. Run [Sample 10](../../../dotnet/samples/DurableAgents/ConsoleApps/10_AutoHistoryRetention/README.md) and the isolated-backend [atomicity checks](../../../dotnet/tests/Microsoft.Agents.AI.DurableTask.IntegrationTests/README.md) before merge/release. Verify cold restart, duplicate correlation delivery, payload expiry and capacity rollback on the actual intended backend.
6. Route genuinely new work to the new namespace. Verify persisted schema 2, mailbox/receipt delivery, truncation evidence and reduced later model input. Treat retention metrics as attempt-level operational evidence, not exactly-once commit confirmation.
7. Retain old deployment and backups until active work, delivery and recovery obligations are satisfied. Any application-curated context seed into a new session is a new generation, not a migrated completion ledger or guaranteed same-session continuation.

Existing 1.0/1.1/1.2 entities remain legacy under KeepAll. Auto rejects them before agent factories or model/provider callbacks, including an empty-looking transcript. Absence of truncation metadata is not proof that earlier completions were never evicted. The internal certified-fixture converter is not a supported public in-place migration API. Pruned/unknown history, old custom providers and unprovable completion indexes require isolation, not a trust callback, empty receipt maps or inferred receipts.

## Rollback and disabling eviction

1. Stop admission to the Auto namespace, drain or preserve inflight work, and export canonical schema-2 state plus provider continuation before changing runtime configuration.
2. Roll back only to a tested mailbox-, fixed-owner-, pending-provider-initialization- and expiry-schedule-aware build that is explicitly authorized to write 2.0. **Changing Auto to KeepAll does not provide that authorization.** Default KeepAll can read committed duplicates but rejects new schema-2 work; it does not downgrade the entity.
3. To reduce or avoid pressure eviction while continuing with the current public API, retain Auto and raise its byte budget only when the backend's actual storage/transport limits safely permit it. This reduces pressure; it is not a guaranteed no-eviction mode, cannot undo prior eviction, and receipts/continuation can still exhaust the protected floor. There is no separate public schema-2 KeepAll writer switch in this feature.
4. Returning to a schema-1-only binary requires the preserved legacy deployment/session or a pre-activation backup restored into a separate isolated namespace with routing fences. Never run a transcript-only writer against schema 2, alter its version header or discard receipts.
5. Missing transcript cannot be recovered from already-expired payloads. Replay can duplicate external tool/model/provider effects and cannot undo previously observed side effects. Plan application-level recovery separately; lossless in-place downgrade is not supported.

KeepAll is backward compatible for old sessions; it is not a rollback recipe for active Auto sessions.
