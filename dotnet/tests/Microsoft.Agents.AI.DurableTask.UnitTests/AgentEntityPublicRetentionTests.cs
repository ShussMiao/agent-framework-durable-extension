// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using AutoHistoryRetention;
using DurableTask.Core.Entities.OperationFormat;
using Microsoft.Agents.AI.DurableTask.State;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.AI;

namespace Microsoft.Agents.AI.DurableTask.Tests.Unit;

public sealed partial class AgentEntityPublicRetentionTests
{
    private static void ConfigureIsolated(DurableAgentsOptions options, bool auto)
    {
        options.DeploymentMode = DurableAgentDeploymentMode.SchemaV2;
        if (auto)
        {
            HistoryRetentionDemo.ConfigureRetention(options);
        }
    }

    [Theory]
    [InlineData(false, DurableAgentHistoryReplayMode.PreloadEntityHistory)]
    [InlineData(true, DurableAgentHistoryReplayMode.PreloadEntityHistory)]
    [InlineData(false, DurableAgentHistoryReplayMode.CurrentRequestOnly)]
    [InlineData(true, DurableAgentHistoryReplayMode.CurrentRequestOnly)]
    public async Task PublicIsolatedGenericOwnersSurviveColdReloadAsync(bool auto, DurableAgentHistoryReplayMode replay)
    {
        ProbeGenericAgent first = new();
        void configure(DurableAgentsOptions options) => ConfigureIsolated(options, auto);
        EntityBatchResult seed = await DispatchAsync(first, null, "old", mailbox: false, replayMode: replay,
            configureOptions: configure);
        Assert.Null(Result(seed).FailureDetails);
        ProbeGenericAgent cold = new();
        EntityBatchResult next = await DispatchAsync(cold, seed.EntityState, "new", mailbox: false, replayMode: replay,
            configureOptions: configure);
        Assert.Null(Result(next).FailureDetails);
        Assert.Equal(2, ReadState(next).Data.CompletionReceipts!.Count);
        Assert.Equal(replay == DurableAgentHistoryReplayMode.PreloadEntityHistory ? 3 : 1, cold.LastMessages.Count);
        if (replay == DurableAgentHistoryReplayMode.CurrentRequestOnly)
        {
            Assert.Empty(ReadState(next).Data.ConversationHistory);
        }
        Assert.NotNull(ReadState(next).Data.Session);
    }

    [Theory]
    [InlineData(false, "owner")]
    [InlineData(true, "owner")]
    [InlineData(false, "missingKey")]
    [InlineData(true, "missingKey")]
    [InlineData(false, "changedKey")]
    [InlineData(true, "changedKey")]
    public async Task PublicIsolatedOwnerMismatchNeverFallsBackToModelAsync(bool auto, string mismatch)
    {
        void configure(DurableAgentsOptions options) => ConfigureIsolated(options, auto);
        EntityBatchResult seed = await DispatchAsync(CreateAgent(new ProbeClient(), new ProbeProvider()), null, "old",
            mailbox: false, configureOptions: configure);
        ProbeClient client = new();
        ProbeProvider provider = new();
        AIAgent replacement = mismatch == "owner" ? new ChatClientAgent(client, name: "agent") : CreateAgent(client, provider);
        string? before = mismatch == "missingKey" ? null : seed.EntityState;
        EntityBatchResult rejected = await DispatchAsync(replacement, before, "next", mailbox: false,
            configureOptions: configure, providerKey: mismatch == "missingKey" ? null : mismatch == "changedKey" ? "different.v1" : "probe.v1");
        Assert.NotNull(Result(rejected).FailureDetails);
        Assert.Equal(before, rejected.EntityState);
        Assert.Empty(Actions(rejected));
        Assert.Equal(0, client.Count);
        Assert.Equal(0, provider.LoadCount);
        Assert.Equal(0, provider.StoreCount);
        EntityBatchResult retry = await DispatchAsync(CreateAgent(client, provider), rejected.EntityState, "next", mailbox: false,
            configureOptions: configure);
        Assert.Null(Result(retry).FailureDetails);
        Assert.Equal(1, client.Count);
        Assert.Equal(DurableAgentStateCompletionReceipt.SucceededOutcome, ReadState(retry).Data.CompletionReceipts!["next"].Outcome);
    }

    [Theory]
    [InlineData(false, "load", false)]
    [InlineData(true, "load", false)]
    [InlineData(false, "lazyLoad", false)]
    [InlineData(true, "lazyLoad", false)]
    [InlineData(false, "storeBefore", false)]
    [InlineData(true, "storeBefore", false)]
    [InlineData(false, "storeAfter", false)]
    [InlineData(true, "storeAfter", false)]
    [InlineData(false, "load", true)]
    [InlineData(true, "load", true)]
    [InlineData(false, "lazyLoad", true)]
    [InlineData(true, "lazyLoad", true)]
    [InlineData(false, "storeBefore", true)]
    [InlineData(true, "storeBefore", true)]
    [InlineData(false, "storeAfter", true)]
    [InlineData(true, "storeAfter", true)]
    public async Task ExternalCallbackFailureHasNoReceiptAndColdSameCorrelationRemainsExecutableAsync(
        bool auto, string stage, bool first)
    {
        void configure(DurableAgentsOptions options) => ConfigureIsolated(options, auto);
        string? original = first ? null : (await DispatchAsync(
            CreateAgent(new ProbeClient(), new ProbeProvider()), null, "seed",
            mailbox: false, configureOptions: configure)).EntityState;
        ProbeProvider provider = new() { Stage = stage, Failure = new IOException(PrivateMarker) };
        ProbeClient client = new();
        ChatClientAgent agent = CreateAgent(client, provider);
        EntityBatchResult failed = await DispatchAsync(agent, original, "retryable", mailbox: false,
            configureOptions: configure);
        Assert.Equal(typeof(IOException).FullName, Failure(failed).ErrorType);
        Assert.Equal(original, failed.EntityState);
        Assert.Empty(Actions(failed));
        Assert.Same(provider, agent.ChatHistoryProvider);
        Assert.Equal(stage is "load" or "lazyLoad" ? 0 : 1, client.Count);
        Assert.Equal(stage == "storeAfter" ? 1 : 0, provider.RemoteMessages.Count);
        Assert.Equal(first ? null : 1, provider.RestoredVersion);
        if (!first)
        {
            DurableAgentState rolledBack = ReadState(failed);
            Assert.False(rolledBack.Data.CompletionReceipts!.ContainsKey("retryable"));
            Assert.False(rolledBack.Data.TerminalResults!.ContainsKey("retryable"));
            Assert.DoesNotContain(PrivateMarker, failed.EntityState, StringComparison.Ordinal);
        }

        ProbeProvider coldProvider = new() { Stage = stage, Failure = new IOException(PrivateMarker) };
        ProbeClient coldClient = new();
        EntityBatchResult failedAgain = await DispatchAsync(CreateAgent(coldClient, coldProvider), failed.EntityState,
            "retryable", mailbox: false, configureOptions: configure);
        Assert.Equal(typeof(IOException).FullName, Failure(failedAgain).ErrorType);
        Assert.Equal(original, failedAgain.EntityState);
        Assert.Empty(Actions(failedAgain));
        Assert.Equal(stage is "load" or "lazyLoad" ? 0 : 1, coldClient.Count);
        Assert.Equal(first ? null : 1, coldProvider.RestoredVersion);

        ProbeProvider recoveredProvider = new();
        ProbeClient recoveredClient = new();
        EntityBatchResult retry = await DispatchAsync(CreateAgent(recoveredClient, recoveredProvider),
            failedAgain.EntityState, "retryable", mailbox: false, configureOptions: configure);
        Assert.Null(Result(retry).FailureDetails);
        Assert.Equal(1, recoveredClient.Count);
        Assert.Equal(first ? null : 1, recoveredProvider.RestoredVersion);
        Assert.Empty(ReadState(retry).Data.ConversationHistory);
        Assert.Equal(DurableAgentStateCompletionReceipt.SucceededOutcome,
            ReadState(retry).Data.CompletionReceipts!["retryable"].Outcome);

        EntityBatchResult duplicate = await DispatchAsync(CreateAgent(recoveredClient, recoveredProvider),
            retry.EntityState, "retryable", mailbox: false, configureOptions: configure);
        Assert.Null(Result(duplicate).FailureDetails);
        Assert.Equal(retry.EntityState, duplicate.EntityState);
        Assert.Equal(1, recoveredClient.Count);
        Assert.Equal(1, recoveredProvider.LoadCount);
        Assert.Equal(1, recoveredProvider.StoreCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreFailureAfterActualToolEffectRollsBackSdkOutboxButColdRetryRepeatsEffectAsync(bool auto)
    {
        void configure(DurableAgentsOptions options) => ConfigureIsolated(options, auto);
        EntityBatchResult seed = await DispatchAsync(CreateAgent(new ProbeClient(), new ProbeProvider()),
            null, "seed", mailbox: false, configureOptions: configure);
        int remoteEffects = 0;
        AIFunction tool = AIFunctionFactory.Create(() =>
        {
            remoteEffects++;
            _ = DurableAgentContext.Current.ScheduleNewOrchestration("tool-work");
            return "accepted externally";
        }, name: "perform_operation");
        ChatClientAgent create(IChatClient client, ProbeProvider provider) => new(client, new ChatClientAgentOptions
        {
            Name = "agent",
            ChatHistoryProvider = provider,
            ChatOptions = new ChatOptions { Tools = [tool] },
        });
        ProbeProvider failingProvider = new() { Stage = "storeAfter", Failure = new IOException(PrivateMarker) };
        ToolLoopClient failingClient = new(tool.Name);
        EntityBatchResult failed = await DispatchAsync(create(failingClient, failingProvider),
            seed.EntityState, "retryable", mailbox: false, configureOptions: configure);
        Assert.Equal(typeof(IOException).FullName, Failure(failed).ErrorType);
        Assert.Equal(seed.EntityState, failed.EntityState);
        Assert.Empty(Actions(failed));
        Assert.Equal(1, remoteEffects);
        Assert.Equal(2, failingClient.Count);
        Assert.Single(failingProvider.RemoteMessages);
        Assert.False(ReadState(failed).Data.CompletionReceipts!.ContainsKey("retryable"));

        ProbeProvider recoveredProvider = new() { RemoteMessages = failingProvider.RemoteMessages };
        ToolLoopClient coldClient = new(tool.Name);
        EntityBatchResult retry = await DispatchAsync(create(coldClient, recoveredProvider),
            failed.EntityState, "retryable", mailbox: false, configureOptions: configure);
        Assert.Null(Result(retry).FailureDetails);
        Assert.Equal(2, remoteEffects);
        Assert.Equal(2, coldClient.Count);
        Assert.Equal(2, recoveredProvider.RemoteMessages.Count);
        Assert.Equal(1, recoveredProvider.RestoredVersion);
        Assert.Equal("tool-work", Assert.IsType<StartNewOrchestrationOperationAction>(Assert.Single(Actions(retry))).Name);
        EntityBatchResult duplicate = await DispatchAsync(create(coldClient, recoveredProvider),
            retry.EntityState, "retryable", mailbox: false, configureOptions: configure);
        Assert.Null(Result(duplicate).FailureDetails);
        Assert.Equal(retry.EntityState, duplicate.EntityState);
        Assert.Empty(Actions(duplicate));
        Assert.Equal(2, remoteEffects);
        Assert.Equal(2, coldClient.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceModelFailurePreservesContinuationWithoutFinalityAndColdRetryExecutesAsync(bool auto)
    {
        void configure(DurableAgentsOptions options) => ConfigureIsolated(options, auto);
        EntityBatchResult seed = await DispatchAsync(
            new ChatClientAgent(new ProbeClient { ResponseConversationId = "remote-conversation" }, name: "agent"),
            null, "seed", mailbox: false, configureOptions: configure);
        ProbeClient failing = new() { Failure = new IOException(PrivateMarker) };
        EntityBatchResult failed = await DispatchAsync(new ChatClientAgent(failing, name: "agent"),
            seed.EntityState, "retryable", mailbox: false, configureOptions: configure);
        Assert.Equal(typeof(IOException).FullName, Failure(failed).ErrorType);
        Assert.Equal(seed.EntityState, failed.EntityState);
        Assert.Empty(Actions(failed));
        Assert.Equal(1, failing.Count);
        Assert.False(ReadState(failed).Data.CompletionReceipts!.ContainsKey("retryable"));

        ProbeClient cold = new() { ResponseConversationId = "remote-conversation" };
        EntityBatchResult retry = await DispatchAsync(new ChatClientAgent(cold, name: "agent"),
            failed.EntityState, "retryable", mailbox: false, configureOptions: configure);
        Assert.Null(Result(retry).FailureDetails);
        Assert.Equal(1, cold.Count);
        Assert.Single(cold.LastMessages);
        Assert.Empty(ReadState(retry).Data.ConversationHistory);
        Assert.Equal("remote-conversation", ReadState(retry).Data.Session!.Value.GetProperty("conversationId").GetString());
    }

    [Fact]
    public async Task PublicIsolatedKeepAllDoesNotPruneTranscriptEvenBeyondAutoDemoBudgetAsync()
    {
        string? state = null;
        ProbeClient client = new();
        for (int turn = 0; turn < HistoryRetentionDemo.ScenarioTurns; turn++)
        {
            EntityBatchResult next = await DispatchAsync(new ChatClientAgent(client, name: "agent"), state, $"turn-{turn}",
                mailbox: false, requestText: new string('x', HistoryRetentionDemo.NotesPerTurn),
                configureOptions: options =>
                {
                    ConfigureIsolated(options, auto: false);
                    options.MaxStateBytes = 1;
                    options.HistoryRetentionHighWatermark = 0.2;
                    options.HistoryRetentionLowWatermark = 0.9;
                });
            Assert.Null(Result(next).FailureDetails);
            state = next.EntityState;
        }
        DurableAgentState retained = Assert.IsType<DurableAgentState>(s_converter.Deserialize(state, typeof(DurableAgentState)));
        Assert.True(DurableAgentStateRetention.GetSerializedSize(retained) > HistoryRetentionDemo.MaxStateBytes);
        Assert.Null(retained.Data.Truncation);
        Assert.Equal(HistoryRetentionDemo.ScenarioTurns * 2, retained.Data.ConversationHistory.Count);
        Assert.Equal(HistoryRetentionDemo.ScenarioTurns, retained.Data.CompletionReceipts!.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicAutoDefaultAndNullBudgetInitializeCanonicalStateAsync(bool explicitNull)
    {
        ProbeClient client = new();
        EntityBatchResult run = await DispatchAsync(new ChatClientAgent(client, name: "agent"), null, "new",
            mailbox: false, configureOptions: options =>
            {
                options.DeploymentMode = DurableAgentDeploymentMode.SchemaV2;
                options.HistoryRetentionMode = DurableAgentHistoryRetentionMode.Auto;
                if (explicitNull)
                {
                    options.MaxStateBytes = null;
                }
                Assert.Equal(1_048_576, options.GetRetentionSettings().MaxStateBytes);
            });
        Assert.Null(Result(run).FailureDetails);
        Assert.Equal(DurableAgentState.RevisedSchemaVersion, ReadState(run).SchemaVersion);
        Assert.Null(ReadState(run).Data.Truncation);
        Assert.Single(ReadState(run).Data.CompletionReceipts!);
        Assert.Equal(1, client.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdKeepAllWriterIgnoresDefaultOrOverriddenBudgetAboveOneMiBAsync(bool overrideBudget)
    {
        void configure(DurableAgentsOptions options)
        {
            ConfigureIsolated(options, auto: false);
            if (overrideBudget)
            {
                options.MaxStateBytes = 1;
                options.HistoryRetentionHighWatermark = 0.2;
                options.HistoryRetentionLowWatermark = 0.9;
            }
        }
        EntityBatchResult seed = await DispatchAsync(new ChatClientAgent(new ProbeClient(), name: "agent"), null, "old",
            mailbox: false, configureOptions: configure);
        ProbeClient cold = new();
        EntityBatchResult next = await DispatchAsync(new ChatClientAgent(cold, name: "agent"), seed.EntityState, "new",
            mailbox: false, configureOptions: configure, requestText: new string('x', 1_048_576 + 1_024));
        Assert.Null(Result(next).FailureDetails);
        DurableAgentState retained = ReadState(next);
        Assert.True(DurableAgentStateRetention.GetSerializedSize(retained) > 1_048_576);
        Assert.Null(retained.Data.Truncation);
        Assert.Equal(4, retained.Data.ConversationHistory.Count);
        Assert.Equal(2, retained.Data.CompletionReceipts!.Count);
        Assert.Equal(1, cold.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchingRetentionKeepsIsolatedCanonicalWriterAuthorityAsync(bool firstAuto)
    {
        EntityBatchResult seed = await DispatchAsync(new ChatClientAgent(new ProbeClient(), name: "agent"), null, "old",
            mailbox: false, configureOptions: options => ConfigureIsolated(options, firstAuto));
        ProbeClient cold = new();
        EntityBatchResult next = await DispatchAsync(new ChatClientAgent(cold, name: "agent"), seed.EntityState, "new",
            mailbox: false, configureOptions: options => ConfigureIsolated(options, !firstAuto));
        Assert.Null(Result(next).FailureDetails);
        Assert.Equal(DurableAgentState.RevisedSchemaVersion, ReadState(next).SchemaVersion);
        Assert.Equal(2, ReadState(next).Data.CompletionReceipts!.Count);
        Assert.Equal(1, cold.Count);
    }

    [Theory]
    [InlineData(false, "entity")]
    [InlineData(true, "entity")]
    [InlineData(false, "external")]
    [InlineData(true, "external")]
    [InlineData(false, "service")]
    [InlineData(true, "service")]
    public async Task PublicIsolatedPoliciesWriteSameMailboxAndRestoreFixedOwnersAsync(bool auto, string owner)
    {
        ProbeProvider provider = new();
        ProbeClient client = new() { ResponseConversationId = owner == "service" ? "remote-conversation" : null };
        ChatClientAgent agent = owner == "external" ? CreateAgent(client, provider) : new(client, name: "agent");
        void configure(DurableAgentsOptions options) => ConfigureIsolated(options, auto);
        EntityBatchResult seed = await DispatchAsync(agent, null, "old", mailbox: false, configureOptions: configure);
        Assert.Null(Result(seed).FailureDetails);
        DurableAgentState state = ReadState(seed);
        Assert.Equal(DurableAgentState.RevisedSchemaVersion, state.SchemaVersion);
        Assert.Single(state.Data.CompletionReceipts!);
        Assert.Null(state.Data.ExpirationTimeUtc);
        Assert.Null(state.Data.Truncation);
        if (owner != "entity")
        {
            Assert.Empty(state.Data.ConversationHistory);
        }

        ProbeClient coldClient = new() { ResponseConversationId = owner == "service" ? "remote-conversation" : null };
        ProbeProvider coldProvider = new();
        ChatClientAgent cold = owner == "external" ? CreateAgent(coldClient, coldProvider) : new(coldClient, name: "agent");
        EntityBatchResult duplicate = await DispatchAsync(cold, seed.EntityState, "old", mailbox: false,
            emptyInput: true, configureOptions: configure);
        Assert.Null(Result(duplicate).FailureDetails);
        Assert.Equal(0, coldClient.Count);
        Assert.Equal(0, coldProvider.LoadCount);
        Assert.Equal(seed.EntityState, duplicate.EntityState);
        EntityBatchResult next = await DispatchAsync(cold, duplicate.EntityState, "next", mailbox: false,
            configureOptions: configure);
        Assert.Null(Result(next).FailureDetails);
        Assert.Equal(2, ReadState(next).Data.CompletionReceipts!.Count);
        Assert.Equal(1, coldClient.Count);
        if (owner == "external")
        {
            Assert.Equal(1, coldProvider.RestoredVersion);
            Assert.Empty(ReadState(next).Data.ConversationHistory);
        }
        if (owner == "service")
        {
            Assert.Single(coldClient.LastMessages);
            Assert.Empty(ReadState(next).Data.ConversationHistory);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicIsolatedExternalStoreFailureRollsBackAndColdRetryRepeatsModelAsync(bool auto)
    {
        ProbeProvider provider = new();
        ProbeClient client = new();
        ChatClientAgent agent = CreateAgent(client, provider);
        void configure(DurableAgentsOptions options) => ConfigureIsolated(options, auto);
        EntityBatchResult seed = await DispatchAsync(agent, null, "old", mailbox: false, configureOptions: configure);
        provider.Stage = "storeAfter";
        provider.Failure = new IOException(PrivateMarker);
        EntityBatchResult failed = await DispatchAsync(agent, seed.EntityState, "failed", mailbox: false,
            configureOptions: configure);
        Assert.Equal(typeof(IOException).FullName, Failure(failed).ErrorType);
        Assert.Equal(seed.EntityState, failed.EntityState);
        Assert.Empty(Actions(failed));
        DurableAgentState state = ReadState(failed);
        Assert.False(state.Data.CompletionReceipts!.ContainsKey("failed"));
        Assert.False(state.Data.TerminalResults!.ContainsKey("failed"));
        Assert.Equal(ReadState(seed).Data.Session!.Value.GetRawText(), state.Data.Session!.Value.GetRawText());
        Assert.Empty(state.Data.ConversationHistory);
        Assert.DoesNotContain(PrivateMarker, failed.EntityState, StringComparison.Ordinal);
        Assert.Equal(2, client.Count);
        Assert.Equal(2, provider.StoreCount);
        ProbeClient retryClient = new();
        ProbeProvider retryProvider = new();
        EntityBatchResult retry = await DispatchAsync(CreateAgent(retryClient, retryProvider), failed.EntityState, "failed", mailbox: false,
            configureOptions: configure);
        Assert.Null(Result(retry).FailureDetails);
        Assert.Equal(1, retryClient.Count);
        Assert.Equal(1, retryProvider.RestoredVersion);
        Assert.Equal(DurableAgentStateCompletionReceipt.SucceededOutcome, ReadState(retry).Data.CompletionReceipts!["failed"].Outcome);
        EntityBatchResult duplicate = await DispatchAsync(CreateAgent(retryClient, retryProvider), retry.EntityState, "failed", mailbox: false,
            configureOptions: configure);
        Assert.Null(Result(duplicate).FailureDetails);
        Assert.Equal(1, retryClient.Count);
        Assert.Equal(1, retryProvider.StoreCount);
        Assert.Equal(retry.EntityState, duplicate.EntityState);
    }

    [Theory]
    [InlineData(false, "1.0.0")]
    [InlineData(true, "1.0.0")]
    [InlineData(false, "1.1.0")]
    [InlineData(true, "1.1.0")]
    [InlineData(false, "1.2.0")]
    [InlineData(true, "1.2.0")]
    public async Task IsolatedPoliciesPreserveEmptyLegacyAcrossExpiredCleanupAndFollowingRunAsync(bool auto, string version)
    {
        string original = $$$""" {"schemaVersion":"{{{version}}}", "data":{"conversationHistory":[], "expirationTimeUtc":"2020-01-01T00:00:00Z"}} """;
        ProbeClient client = new();
        ProbeProvider provider = new();
        ChatClientAgent agent = CreateAgent(client, provider);
        void configure(DurableAgentsOptions options) => ConfigureIsolated(options, auto);
        EntityBatchResult direct = await DispatchAsync(agent, original, "old", mailbox: false, configureOptions: configure);
        Assert.Equal(typeof(DurableAgentStateCorruptionException).FullName, Failure(direct).ErrorType);
        foreach (string operation in new[] { nameof(AgentEntity.CheckAndDeleteIfExpired), nameof(AgentEntity.CheckAndExpireResults) })
        {
            EntityBatchResult cleanup = await DispatchAsync(agent, original, "unused", mailbox: false,
                operationName: operation, configureOptions: configure);
            Assert.Null(Result(cleanup).FailureDetails);
            Assert.Equal(original, cleanup.EntityState);
            Assert.Empty(Actions(cleanup));
            EntityBatchResult following = await DispatchAsync(agent, cleanup.EntityState, "old", mailbox: false,
                configureOptions: configure);
            Assert.Equal(typeof(DurableAgentStateCorruptionException).FullName, Failure(following).ErrorType);
            Assert.Equal(original, following.EntityState);
            Assert.Empty(Actions(following));
        }
        Assert.Equal(0, client.Count);
        Assert.Equal(0, provider.LoadCount);
        Assert.Equal(0, provider.StoreCount);
    }

    [Fact]
    public async Task LegacyRollbackReadsSchema2WithoutWriteBackAndRejectsNewWorkAsync()
    {
        EntityBatchResult seed = await DispatchAsync(new ChatClientAgent(new ProbeClient(), name: "agent"), null, "old",
            mailbox: false, configureOptions: options => ConfigureIsolated(options, auto: false));
        string raw = " \n" + seed.EntityState + "\n ";
        ProbeClient client = new();
        ChatClientAgent agent = new(client, name: "agent");
        EntityBatchResult duplicate = await DispatchAsync(agent, raw, "old", mailbox: false, emptyInput: true);
        Assert.Null(Result(duplicate).FailureDetails);
        Assert.Equal(raw, duplicate.EntityState);
        Assert.Empty(Actions(duplicate));
        EntityBatchResult next = await DispatchAsync(agent, raw, "new", mailbox: false);
        Assert.Equal(typeof(InvalidOperationException).FullName, Failure(next).ErrorType);
        Assert.Equal(raw, next.EntityState);
        Assert.Empty(Actions(next));
        Assert.Equal(0, client.Count);
    }

    [Fact]
    public async Task UnacknowledgedAutoFailsBeforeSdkInitializationOrCallbacksAsync()
    {
        ProbeClient client = new();
        ProbeProvider provider = new();
        EntityBatchResult rejected = await DispatchAsync(CreateAgent(client, provider), null, "new", mailbox: false,
            configureOptions: options =>
            {
                options.HistoryRetentionMode = DurableAgentHistoryRetentionMode.Auto;
                options.MaxStateBytes = 32 * 1024;
            });
        Assert.Equal(typeof(InvalidOperationException).FullName, Failure(rejected).ErrorType);
        Assert.Null(rejected.EntityState);
        Assert.Empty(Actions(rejected));
        Assert.Equal(0, client.Count);
        Assert.Equal(0, provider.LoadCount);
        Assert.Equal(0, provider.StoreCount);
    }

    [Fact]
    public async Task PublicAutoEvictsRealSdkStateAndColdModelInputWithoutInternalAuthorizationAsync()
    {
        const string Marker = "ORIGINAL-PUBLIC-MARKER";
        ProbeClient client = new();
        string? state = null;
        for (int turn = 1; turn <= HistoryRetentionDemo.ScenarioTurns; turn++)
        {
            EntityBatchResult batch = await DispatchAsync(
                new ChatClientAgent(client, name: "agent"),
                state,
                $"turn-{turn}",
                mailbox: false,
                configureOptions: HistoryRetentionDemo.ConfigureRetention,
                requestText: turn == 1
                    ? HistoryRetentionDemo.CreateFirstNote("sample", Marker, new string('a', HistoryRetentionDemo.NotesPerTurn))
                    : HistoryRetentionDemo.CreateLaterNote("sample", turn, new string('b', HistoryRetentionDemo.NotesPerTurn)));
            Assert.Null(batch.FailureDetails);
            Assert.Null(Result(batch).FailureDetails);
            Assert.True(DurableAgentStateRetention.GetSerializedSize(ReadState(batch)) < HistoryRetentionDemo.HighWatermarkBytes);
            state = batch.EntityState;
        }

        DurableAgentState committed = Assert.IsType<DurableAgentState>(
            s_converter.Deserialize(state, typeof(DurableAgentState)));
        Assert.Equal(DurableAgentState.RevisedSchemaVersion, committed.SchemaVersion);
        Assert.NotNull(committed.Data.Truncation);
        Assert.True(committed.Data.Truncation.EvictedMessageCount.GetInt64() > 0);
        Assert.DoesNotContain(committed.Data.ConversationHistory, entry => entry.CorrelationId == "turn-1");
        Assert.Equal(HistoryRetentionDemo.ScenarioTurns, committed.Data.CompletionReceipts!.Count);
        Assert.Equal(HistoryRetentionDemo.ScenarioTurns, committed.Data.TerminalResults!.Count);
        Assert.Null(committed.Data.ExpirationTimeUtc);

        ProbeClient coldClient = new();
        EntityBatchResult duplicate = await DispatchAsync(
            new ChatClientAgent(coldClient, name: "agent"), state, "turn-1",
            mailbox: false, emptyInput: true, configureOptions: HistoryRetentionDemo.ConfigureRetention);
        Assert.Null(Result(duplicate).FailureDetails);
        Assert.Equal("response", Assert.IsType<AgentResponse>(
            s_converter.Deserialize(Result(duplicate).Result, typeof(AgentResponse))).Text);
        Assert.Equal(0, coldClient.Count);

        EntityBatchResult next = await DispatchAsync(
            new ChatClientAgent(coldClient, name: "agent"), duplicate.EntityState, "diagnostic",
            mailbox: false, configureOptions: HistoryRetentionDemo.ConfigureRetention,
            requestText: HistoryRetentionDemo.DiagnosticQuestion);
        Assert.Null(Result(next).FailureDetails);
        Assert.Equal(1, coldClient.Count);
        Assert.DoesNotContain(coldClient.LastMessages, message => message.Text?.Contains(Marker, StringComparison.Ordinal) is true);
        Assert.Equal(HistoryRetentionDemo.ScenarioTurns + 1, ReadState(next).Data.CompletionReceipts!.Count);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    [InlineData("1.2.0")]
    public async Task DefaultPublicConfigurationKeepsLegacyWriterAcrossSkippedVersionsAsync(string version)
    {
        DurableAgentState legacy = new()
        {
            SchemaVersion = version,
            Data = new()
            {
                ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["preserved"] = JsonSerializer.SerializeToElement(new { value = false }),
                },
            },
        };
        ProbeClient client = new();
        EntityBatchResult batch = await DispatchAsync(
            new ChatClientAgent(client, name: "agent"), s_converter.Serialize(legacy), "new", mailbox: false);

        Assert.Null(Result(batch).FailureDetails);
        DurableAgentState written = ReadState(batch);
        Assert.Equal(DurableAgentState.CurrentSchemaVersion, written.SchemaVersion);
        Assert.Null(written.Data.CompletionReceipts);
        Assert.Null(written.Data.TerminalResults);
        Assert.False(written.Data.ExtensionData!["preserved"].GetProperty("value").GetBoolean());
        Assert.Equal(1, client.Count);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("used")]
    [InlineData("truncated")]
    [InlineData("compacted")]
    [InlineData("session")]
    [InlineData("opaque")]
    public async Task PublicAutoNeverCertifiesPersistedLegacyStateFromItsAppearanceAsync(string kind)
    {
        DurableAgentState legacy = new();
        if (kind == "used")
        {
            legacy.Data.ConversationHistory.Add(DurableAgentStateResponse.FromResponse(
                "old", new AgentResponse([new ChatMessage(ChatRole.Assistant, "original result")])));
        }
        if (kind == "truncated")
        {
            legacy.Data.Truncation = new()
            {
                EvictedMessageCount = JsonSerializer.SerializeToElement(1),
                FirstEvictedAt = s_now,
                LastEvictedAt = s_now,
            };
        }
        if (kind == "compacted")
        {
            legacy.Data.ConversationHistory.Add(new DurableAgentStateCompaction());
        }
        if (kind == "session")
        {
            legacy.Data.Session = JsonSerializer.SerializeToElement(new { stateBag = new { continuation = "original" } });
        }
        if (kind == "opaque")
        {
            legacy.UnknownProperties = new Dictionary<string, JsonElement>
            {
                ["priorState"] = JsonSerializer.SerializeToElement(new { complete = false }),
            };
        }
        string original = s_converter.Serialize(legacy);
        ProbeClient client = new();

        EntityBatchResult rejected = await DispatchAsync(
            new ChatClientAgent(client, name: "agent"), original, kind == "used" ? "old" : "new",
            mailbox: false, configureOptions: HistoryRetentionDemo.ConfigureRetention);

        Assert.Equal(typeof(DurableAgentStateCorruptionException).FullName, Failure(rejected).ErrorType);
        Assert.Equal(original, rejected.EntityState);
        Assert.Empty(Actions(rejected));
        Assert.Equal(0, client.Count);
    }

    [Fact]
    public async Task PublicAutoResultsExpireWithoutReopeningColdCorrelationAsync()
    {
        EntityBatchResult seed = await DispatchAsync(
            new ChatClientAgent(new ProbeClient(), name: "agent"), null, "old",
            mailbox: false, configureOptions: HistoryRetentionDemo.ConfigureRetention);
        EntityBatchResult cleaned = await DispatchAsync(
            new ChatClientAgent(new ProbeClient(), name: "agent"), seed.EntityState, "unused",
            mailbox: false, operationName: nameof(AgentEntity.CheckAndExpireResults),
            now: s_now.AddMinutes(2), configureOptions: HistoryRetentionDemo.ConfigureRetention);

        Assert.Null(Result(cleaned).FailureDetails);
        DurableAgentState state = ReadState(cleaned);
        Assert.Empty(state.Data.TerminalResults!);
        Assert.Equal(DurableAgentStateCompletionReceipt.UnavailableResult, state.Data.CompletionReceipts!["old"].ResultState);
        ProbeClient coldClient = new();
        EntityBatchResult duplicate = await DispatchAsync(
            new ChatClientAgent(coldClient, name: "agent"), cleaned.EntityState, "old",
            mailbox: false, now: s_now.AddMinutes(2), configureOptions: HistoryRetentionDemo.ConfigureRetention);
        Assert.Equal(typeof(DurableAgentResultUnavailableException).FullName, Failure(duplicate).ErrorType);
        Assert.Equal(cleaned.EntityState, duplicate.EntityState);
        Assert.Equal(0, coldClient.Count);
    }

    [Theory]
    [InlineData(nameof(AgentEntity.CheckAndExpireResults), "1.0.0", false)]
    [InlineData(nameof(AgentEntity.CheckAndExpireResults), "1.1.0", false)]
    [InlineData(nameof(AgentEntity.CheckAndExpireResults), "1.2.0", false)]
    [InlineData(nameof(AgentEntity.CheckAndDeleteIfExpired), "1.0.0", false)]
    [InlineData(nameof(AgentEntity.CheckAndDeleteIfExpired), "1.1.0", false)]
    [InlineData(nameof(AgentEntity.CheckAndDeleteIfExpired), "1.2.0", false)]
    [InlineData(nameof(AgentEntity.CheckAndExpireResults), "1.0.0", true)]
    [InlineData(nameof(AgentEntity.CheckAndExpireResults), "1.1.0", true)]
    [InlineData(nameof(AgentEntity.CheckAndExpireResults), "1.2.0", true)]
    [InlineData(nameof(AgentEntity.CheckAndDeleteIfExpired), "1.0.0", true)]
    [InlineData(nameof(AgentEntity.CheckAndDeleteIfExpired), "1.1.0", true)]
    [InlineData(nameof(AgentEntity.CheckAndDeleteIfExpired), "1.2.0", true)]
    public async Task PublicAutoCleanupPreservesPersistedEmptyLegacyAndCannotAuthorizeFollowingRunAsync(
        string operationName, string version, bool auto)
    {
        string original = $$$"""{"schemaVersion":"{{{version}}}","data":{"conversationHistory":[]}}""";
        const string Correlation = "unproven-old-correlation";
        ProbeClient client = new();
        ProbeProvider provider = new();
        ChatClientAgent agent = CreateAgent(client, provider);
        void configure(DurableAgentsOptions options) => ConfigureIsolated(options, auto);

        EntityBatchResult direct = await DispatchAsync(
            agent, original, Correlation, mailbox: false, configureOptions: configure);
        Assert.Equal(typeof(DurableAgentStateCorruptionException).FullName, Failure(direct).ErrorType);
        Assert.Equal(original, direct.EntityState);
        Assert.Empty(Actions(direct));

        EntityBatchResult cleanup = await DispatchAsync(
            agent, original, Correlation, mailbox: false, operationName: operationName,
            configureOptions: configure);
        Assert.Null(Result(cleanup).FailureDetails);
        Assert.Equal(original, cleanup.EntityState);
        Assert.Empty(Actions(cleanup));
        Assert.Null(ReadState(cleanup).Data.CompletionReceipts);

        EntityBatchResult following = await DispatchAsync(
            agent, cleanup.EntityState, Correlation, mailbox: false,
            configureOptions: configure);
        Assert.Equal(typeof(DurableAgentStateCorruptionException).FullName, Failure(following).ErrorType);
        Assert.Equal(original, following.EntityState);
        Assert.Empty(Actions(following));
        Assert.Null(ReadState(following).Data.CompletionReceipts);

        string noncanonical = " \n" + original.Replace("\":", "\" : ", StringComparison.Ordinal) + "\n ";
        EntityBatchResult batched = await DispatchAsync(
            agent, noncanonical, Correlation, mailbox: false,
            configureOptions: configure,
            operations:
            [
                new OperationRequest { Id = Guid.NewGuid(), Operation = operationName },
                new OperationRequest
                {
                    Id = Guid.NewGuid(),
                    Operation = nameof(AgentEntity.Run),
                    Input = s_converter.Serialize(new RunRequest("request") { CorrelationId = Correlation }),
                },
            ]);
        List<OperationResult> results = Assert.IsType<List<OperationResult>>(batched.Results);
        Assert.Equal(2, results.Count);
        Assert.Null(results[0].FailureDetails);
        Assert.Equal(typeof(DurableAgentStateCorruptionException).FullName, results[1].FailureDetails?.ErrorType);
        Assert.Equal(noncanonical, batched.EntityState);
        Assert.Empty(Actions(batched));
        Assert.Null(ReadState(batched).Data.CompletionReceipts);
        Assert.Equal(0, client.Count);
        Assert.Equal(0, provider.LoadCount);
        Assert.Equal(0, provider.StoreCount);
    }

    [Theory]
    [InlineData(nameof(AgentEntity.CheckAndExpireResults), false, false)]
    [InlineData(nameof(AgentEntity.CheckAndExpireResults), false, true)]
    [InlineData(nameof(AgentEntity.CheckAndExpireResults), true, true)]
    [InlineData(nameof(AgentEntity.CheckAndDeleteIfExpired), false, false)]
    [InlineData(nameof(AgentEntity.CheckAndDeleteIfExpired), false, true)]
    [InlineData(nameof(AgentEntity.CheckAndDeleteIfExpired), true, true)]
    public async Task MissingCleanupLeavesNoGhostAndFollowingSdkBatchRunOwnsItsGenerationAsync(
        string operationName, bool auto, bool isolated)
    {
        ProbeClient client = new();
        ChatClientAgent agent = new(client, name: "agent");
        Action<DurableAgentsOptions>? configure = isolated ? options => ConfigureIsolated(options, auto) : null;
        EntityBatchResult missing = await DispatchAsync(
            agent, null, "fresh", mailbox: false, operationName: operationName, configureOptions: configure);
        Assert.Null(Result(missing).FailureDetails);
        Assert.Null(missing.EntityState);
        Assert.Empty(Actions(missing));
        Assert.Equal(0, client.Count);

        EntityBatchResult batch = await DispatchAsync(
            agent, null, "fresh", mailbox: false, configureOptions: configure,
            operations:
            [
                new OperationRequest { Id = Guid.NewGuid(), Operation = operationName },
                new OperationRequest
                {
                    Id = Guid.NewGuid(),
                    Operation = nameof(AgentEntity.Run),
                    Input = s_converter.Serialize(new RunRequest("request") { CorrelationId = "fresh" }),
                },
                new OperationRequest { Id = Guid.NewGuid(), Operation = operationName },
            ]);
        Assert.Null(batch.FailureDetails);
        List<OperationResult> results = Assert.IsType<List<OperationResult>>(batch.Results);
        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.Null(result.FailureDetails));
        DurableAgentState committed = ReadState(batch);
        Assert.Equal(isolated ? DurableAgentState.RevisedSchemaVersion : DurableAgentState.CurrentSchemaVersion,
            committed.SchemaVersion);
        Assert.Equal(1, client.Count);
        if (isolated)
        {
            Assert.Equal(DurableAgentStateCompletionReceipt.SucceededOutcome,
                committed.Data.CompletionReceipts!["fresh"].Outcome);
        }
        else
        {
            Assert.Null(committed.Data.CompletionReceipts);
            Assert.NotEmpty(committed.Data.ConversationHistory);
        }
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    [InlineData("1.2.0")]
    public async Task KeepAllSdkCleanupStillDeletesExpiredLegacyGenerationAsync(string version)
    {
        DurableAgentState legacy = new() { SchemaVersion = version };
        legacy.Data.ExpirationTimeUtc = s_now.AddMinutes(-1).UtcDateTime;
        ProbeClient client = new();
        ProbeProvider provider = new();
        EntityBatchResult expired = await DispatchAsync(
            CreateAgent(client, provider), s_converter.Serialize(legacy), "unused", mailbox: false,
            operationName: nameof(AgentEntity.CheckAndDeleteIfExpired),
            configureOptions: options => options.DefaultTimeToLive = TimeSpan.FromMinutes(5));

        Assert.Null(Result(expired).FailureDetails);
        Assert.Null(expired.EntityState);
        Assert.Empty(Actions(expired));
        Assert.Equal(0, client.Count);
        Assert.Equal(0, provider.LoadCount);
        Assert.Equal(0, provider.StoreCount);
    }

    [Fact]
    public async Task TurningAutoOffPreservesReadOnlyDuplicatesButRejectsNewSchema2WorkAsync()
    {
        EntityBatchResult seed = await DispatchAsync(
            new ChatClientAgent(new ProbeClient(), name: "agent"), null, "old",
            mailbox: false, configureOptions: HistoryRetentionDemo.ConfigureRetention);
        ProbeClient client = new();
        ChatClientAgent agent = new(client, name: "agent");
        EntityBatchResult duplicate = await DispatchAsync(agent, seed.EntityState, "old", mailbox: false);
        Assert.Null(Result(duplicate).FailureDetails);
        Assert.Equal(seed.EntityState, duplicate.EntityState);

        EntityBatchResult rejected = await DispatchAsync(agent, duplicate.EntityState, "new", mailbox: false);
        Assert.Equal(typeof(InvalidOperationException).FullName, Failure(rejected).ErrorType);
        Assert.Equal(seed.EntityState, rejected.EntityState);
        Assert.Empty(Actions(rejected));
        Assert.Equal(0, client.Count);
    }

    [Fact]
    public async Task PublicAutoProtectedFloorRejectsBeforeInvocationAndSdkOutboxCommitAsync()
    {
        ProbeClient client = new();
        EntityBatchResult rejected = await DispatchAsync(
            new ChatClientAgent(client, name: "agent"), null, "new",
            mailbox: false, requestText: new string('x', 2_000),
            configureOptions: options =>
            {
                options.DeploymentMode = DurableAgentDeploymentMode.SchemaV2;
                options.HistoryRetentionMode = DurableAgentHistoryRetentionMode.Auto;
                options.MaxStateBytes = 500;
            });

        Assert.Equal(typeof(DurableAgentStateSizeLimitExceededException).FullName, Failure(rejected).ErrorType);
        Assert.Null(rejected.EntityState);
        Assert.Empty(Actions(rejected));
        Assert.Equal(0, client.Count);
    }

    [Theory]
    [InlineData("response")]
    [InlineData("state")]
    [InlineData("afterSuccessfulDispatch")]
    public async Task PublicAutoSdkCommitFailureRollsBackStateAndSignalsAsync(string boundary)
    {
        EntityBatchResult seed = await DispatchAsync(
            new ChatClientAgent(new ProbeClient(), name: "agent"), null, "old",
            mailbox: false, configureOptions: HistoryRetentionDemo.ConfigureRetention);
        ProbeClient client = new();
        EntityBatchResult rejected = await DispatchAsync(
            new ChatClientAgent(client, name: "agent"), seed.EntityState, "new",
            mailbox: false, failBoundary: boundary, configureOptions: HistoryRetentionDemo.ConfigureRetention);

        Assert.NotNull(Result(rejected).FailureDetails ?? rejected.FailureDetails);
        Assert.Equal(seed.EntityState, rejected.EntityState);
        Assert.Empty(Actions(rejected));
        Assert.Equal(1, client.Count);
        Assert.False(ReadState(rejected).Data.CompletionReceipts!.ContainsKey("new"));
    }

    [Fact]
    public async Task PublicAutoExternalOwnerPreservesStoreAndFailureContinuationAsync()
    {
        ProbeProvider provider = new();
        ProbeClient client = new();
        EntityBatchResult seed = await DispatchAsync(
            CreateAgent(client, provider), null, "old",
            mailbox: false, configureOptions: HistoryRetentionDemo.ConfigureRetention);
        DurableAgentState original = ReadState(seed);
        Assert.Empty(original.Data.ConversationHistory);
        provider.Stage = "storeAfter";
        provider.Failure = new IOException(PrivateMarker);

        EntityBatchResult failed = await DispatchAsync(
            CreateAgent(client, provider), seed.EntityState, "failed",
            mailbox: false, configureOptions: HistoryRetentionDemo.ConfigureRetention);
        Assert.Equal(typeof(IOException).FullName, Failure(failed).ErrorType);
        Assert.Equal(seed.EntityState, failed.EntityState);
        Assert.Empty(Actions(failed));
        DurableAgentState written = ReadState(failed);
        Assert.Empty(written.Data.ConversationHistory);
        Assert.Equal(original.Data.Session!.Value.GetRawText(), written.Data.Session!.Value.GetRawText());
        Assert.Equal(original.Data.HistoryBinding.GetRawText(), written.Data.HistoryBinding.GetRawText());
        Assert.False(written.Data.CompletionReceipts!.ContainsKey("failed"));
        Assert.False(written.Data.TerminalResults!.ContainsKey("failed"));
        Assert.Equal(2, provider.StoreCount);
        Assert.DoesNotContain(PrivateMarker, failed.EntityState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicAutoServiceOwnerRestoresOnlyRemoteContinuationAsync()
    {
        ProbeClient firstClient = new() { ResponseConversationId = "service-conversation" };
        EntityBatchResult seed = await DispatchAsync(
            new ChatClientAgent(firstClient, name: "agent"), null, "old",
            mailbox: false, configureOptions: HistoryRetentionDemo.ConfigureRetention);
        Assert.Empty(ReadState(seed).Data.ConversationHistory);
        Assert.Equal("service-conversation", ReadState(seed).Data.Session!.Value.GetProperty("conversationId").GetString());

        ProbeClient coldClient = new() { ResponseConversationId = "service-conversation" };
        EntityBatchResult next = await DispatchAsync(
            new ChatClientAgent(coldClient, name: "agent"), seed.EntityState, "new",
            mailbox: false, configureOptions: HistoryRetentionDemo.ConfigureRetention);
        Assert.Null(Result(next).FailureDetails);
        Assert.Single(coldClient.LastMessages);
        Assert.Empty(ReadState(next).Data.ConversationHistory);
        Assert.Equal(2, ReadState(next).Data.CompletionReceipts!.Count);
    }

    [Fact]
    public async Task PublicAutoProtectsOpaqueExternalStateInsteadOfDeletingProviderEvidenceAsync()
    {
        EntityBatchResult seed = await DispatchAsync(
            CreateAgent(new ProbeClient(), new ProbeProvider()), null, "old",
            mailbox: false, configureOptions: HistoryRetentionDemo.ConfigureRetention);
        DurableAgentState existing = ReadState(seed);
        existing.UnknownProperties = new Dictionary<string, JsonElement>
        {
            ["opaqueProtectedMetadata"] = JsonSerializer.SerializeToElement(new string('x', 5_000)),
        };
        string original = s_converter.Serialize(existing);
        ProbeClient client = new();
        ProbeProvider provider = new();
        EntityBatchResult rejected = await DispatchAsync(
            CreateAgent(client, provider), original, "new", mailbox: false,
            configureOptions: options =>
            {
                options.DeploymentMode = DurableAgentDeploymentMode.SchemaV2;
                options.HistoryRetentionMode = DurableAgentHistoryRetentionMode.Auto;
                options.MaxStateBytes = 2_000;
            });

        Assert.Equal(typeof(DurableAgentStateSizeLimitExceededException).FullName, Failure(rejected).ErrorType);
        Assert.Equal(original, rejected.EntityState);
        Assert.Empty(Actions(rejected));
        Assert.Equal(0, client.Count);
        Assert.Equal(0, provider.LoadCount);
        Assert.Equal(0, provider.StoreCount);
    }

    [Fact]
    public async Task PublicAutoDoesNotApplyLegacyDefaultIdleDeletionAsync()
    {
        EntityBatchResult seed = await DispatchAsync(
            new ChatClientAgent(new ProbeClient(), name: "agent"), null, "old", mailbox: false,
            configureOptions: options =>
            {
                HistoryRetentionDemo.ConfigureRetention(options);
                options.DefaultTimeToLive = TimeSpan.FromDays(14);
            });
        Assert.Null(ReadState(seed).Data.ExpirationTimeUtc);
        Assert.Single(ReadState(seed).Data.CompletionReceipts!);
    }

    private sealed class FailAfterPublicDispatch(ITaskEntity inner, string correlationId) : ITaskEntity
    {
        public async ValueTask<object?> RunAsync(TaskEntityOperation operation)
        {
            _ = await inner.RunAsync(operation);
            DurableAgentState staged = Assert.IsType<DurableAgentState>(
                operation.State.GetState(typeof(DurableAgentState)));
            Assert.Equal(DurableAgentStateCompletionReceipt.SucceededOutcome,
                staged.Data.CompletionReceipts![correlationId].Outcome);
            throw new CommitFailureException();
        }
    }

    private sealed class ProbeGenericAgent : AIAgent
    {
        public override string Name => "agent";
        public List<ChatMessage> LastMessages { get; private set; } = [];
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
            new(new ProbeSession());
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            new(JsonSerializer.SerializeToElement(new { stateBag = session.StateBag.Serialize() }));
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            new(new ProbeSession());
        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages,
            AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
            AgentSession? session = null, AgentRunOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.LastMessages = messages.ToList();
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.Assistant, "response");
        }
        private sealed class ProbeSession : AgentSession;
    }
}
