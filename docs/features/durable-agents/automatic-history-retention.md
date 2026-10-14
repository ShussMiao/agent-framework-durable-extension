# Automatic durable-history retention in .NET

Deployment acknowledgement and retention are independent. `Legacy` and `KeepAll` remain the .NET defaults: new state is schema 1.2 and no transcript is proactively removed. An acknowledged `IsolatedV2` deployment writes canonical 2.0 with either KeepAll or Auto.

Before activation, follow the shared [C# and Python schema-v2 migration guide](schema-v2-migration.md). It is the single operator guide for reader/fleet preparation, exact code/environment resolution, legacy preservation, routing, validation and rollback.

```csharp
services.ConfigureDurableAgents(options =>
{
    options.DeploymentMode = DurableAgentDeploymentMode.IsolatedV2;
    options.HistoryRetentionMode = DurableAgentHistoryRetentionMode.Auto;
    // Optional override; omitted or null uses 1,048,576 bytes (1 MiB).
    options.MaxStateBytes = 32 * 1024;
    options.HistoryRetentionHighWatermark = 0.85;
    options.HistoryRetentionLowWatermark = 0.70;
    options.AddAIAgent(agent);
});
```

Instead of assigning `DeploymentMode`, the exact environment value `DURABLE_AGENTS_DEPLOYMENT_MODE=isolated_v2` can supply the acknowledgement. It does not prove routing or isolation. Auto without it or the code acknowledgement is rejected before factory/transport/model/provider execution or state mutation.

## Budget and ownership

Auto measures the full UTF-8 escaped JSON-string storage envelope, not token count, raw transcript size or Python's budget accounting. At the high watermark it removes an oldest prefix of eligible atomic transcript components toward low. A write may succeed below high without reaching low when protected components prevent further eviction.

Auto defaults to exactly 1,048,576 bytes (1 MiB); a positive override changes that budget, while
explicit null uses the default rather than disabling Auto. Auto watermarks satisfy `0 < low < high <= 1`.
KeepAll ignores budget/watermark settings and never prunes because they are configured. Property domains
remain positive-or-null for budget and finite fractions in (0, 1] for individual watermarks; their relationship
is checked only under Auto. Backend/provider limits still apply under either policy.

The newest exchange, system messages and connected correlation/tool groups are protected, along with mailbox results, permanent receipts, binding, continuation, bookkeeping and unknown JSON. A protected floor that cannot fit fails closed; external effects already made are not undone. Receipt growth is unbounded.

| Owner | Both isolated policies |
| --- | --- |
| Implicit default ChatClientAgent or generic PreloadEntityHistory | Entity transcript supplies context; only Auto can evict eligible groups |
| Custom external provider | Stable provider key and restorable declared continuation; no transcript mirror or remote deletion |
| Model-service conversation | Stable logical key and real restored conversation ID; no remote deletion |
| Generic CurrentRequestOnly/opaque session | Explicit logical provider key; opaque session protected |
| Explicit in-memory provider with reducers/filters or local per-service-call history | Rejected by the fixed-owner contract rather than silently replaced |

Result payload expiry is separate from transcript retention and preserves receipts. Schema-2 whole-entity TTL deletion is disabled. Auto does not implement FollowCompaction or model-context summarization.

## External callback failures are not terminal completion

External providers and service-managed history remain allowed under both isolated KeepAll and Auto.
Unrecovered external load/store callback errors propagate and roll back local state/outgoing signals;
they do not create a framework failed receipt. A load error before model invocation leaves that model
uncalled; a store error after execution can follow model/tool effects or a successful remote write.
Retrying the same uncommitted correlation can execute again. Cancellation, timeout and response loss
can also leave effects ambiguous. Successful committed receipts still deduplicate completed requests,
but cannot protect uncommitted side effects.

The application owns idempotency and reconciliation. See the shared guide's
[operation-key, ledger, remote-lookup and outbox guidance](schema-v2-migration.md#make-retries-safe-in-your-application).
Provider-failure finalization is tracked separately in
[the provider retry-risk limitation](https://github.com/microsoft/agent-framework-durable-extension/issues/143).

Use [Sample 10](../../../dotnet/samples/DurableAgents/ConsoleApps/10_AutoHistoryRetention/README.md) for the bounded 32 KiB/.85/.70 demonstration. Configure measured production budgets against the real backend and validate atomicity, restart and duplicate delivery there; offline tests are not backend certification.
