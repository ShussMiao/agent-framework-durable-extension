// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using AutoHistoryRetention;
using DurableTask.Core.Entities.OperationFormat;
using Microsoft.Agents.AI.DurableTask.State;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.AI;

namespace Microsoft.Agents.AI.DurableTask.Tests.Unit;

public sealed partial class AgentEntityProviderFailureTests
{
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
    [InlineData(nameof(AgentEntity.CheckAndExpireResults), "1.0.0")]
    [InlineData(nameof(AgentEntity.CheckAndExpireResults), "1.1.0")]
    [InlineData(nameof(AgentEntity.CheckAndExpireResults), "1.2.0")]
    [InlineData(nameof(AgentEntity.CheckAndDeleteIfExpired), "1.0.0")]
    [InlineData(nameof(AgentEntity.CheckAndDeleteIfExpired), "1.1.0")]
    [InlineData(nameof(AgentEntity.CheckAndDeleteIfExpired), "1.2.0")]
    public async Task PublicAutoCleanupPreservesPersistedEmptyLegacyAndCannotAuthorizeFollowingRunAsync(
        string operationName, string version)
    {
        string original = $$$"""{"schemaVersion":"{{{version}}}","data":{"conversationHistory":[]}}""";
        const string Correlation = "unproven-old-correlation";
        ProbeClient client = new();
        ProbeProvider provider = new();
        ChatClientAgent agent = CreateAgent(client, provider);

        EntityBatchResult direct = await DispatchAsync(
            agent, original, Correlation, mailbox: false, configureOptions: HistoryRetentionDemo.ConfigureRetention);
        Assert.Equal(typeof(DurableAgentStateCorruptionException).FullName, Failure(direct).ErrorType);
        Assert.Equal(original, direct.EntityState);
        Assert.Empty(Actions(direct));

        EntityBatchResult cleanup = await DispatchAsync(
            agent, original, Correlation, mailbox: false, operationName: operationName,
            configureOptions: HistoryRetentionDemo.ConfigureRetention);
        Assert.Null(Result(cleanup).FailureDetails);
        Assert.Equal(original, cleanup.EntityState);
        Assert.Empty(Actions(cleanup));
        Assert.Null(ReadState(cleanup).Data.CompletionReceipts);

        EntityBatchResult following = await DispatchAsync(
            agent, cleanup.EntityState, Correlation, mailbox: false,
            configureOptions: HistoryRetentionDemo.ConfigureRetention);
        Assert.Equal(typeof(DurableAgentStateCorruptionException).FullName, Failure(following).ErrorType);
        Assert.Equal(original, following.EntityState);
        Assert.Empty(Actions(following));
        Assert.Null(ReadState(following).Data.CompletionReceipts);

        string noncanonical = " \n" + original.Replace("\":", "\" : ", StringComparison.Ordinal) + "\n ";
        EntityBatchResult batched = await DispatchAsync(
            agent, noncanonical, Correlation, mailbox: false,
            configureOptions: HistoryRetentionDemo.ConfigureRetention,
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
    [InlineData(nameof(AgentEntity.CheckAndExpireResults), false)]
    [InlineData(nameof(AgentEntity.CheckAndExpireResults), true)]
    [InlineData(nameof(AgentEntity.CheckAndDeleteIfExpired), false)]
    [InlineData(nameof(AgentEntity.CheckAndDeleteIfExpired), true)]
    public async Task MissingCleanupLeavesNoGhostAndFollowingSdkBatchRunOwnsItsGenerationAsync(
        string operationName, bool auto)
    {
        ProbeClient client = new();
        ChatClientAgent agent = new(client, name: "agent");
        Action<DurableAgentsOptions>? configure = auto ? HistoryRetentionDemo.ConfigureRetention : null;
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
        Assert.Equal(auto ? DurableAgentState.RevisedSchemaVersion : DurableAgentState.CurrentSchemaVersion,
            committed.SchemaVersion);
        Assert.Equal(1, client.Count);
        if (auto)
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
        Assert.Null(Result(failed).FailureDetails);
        DurableAgentState written = ReadState(failed);
        Assert.Empty(written.Data.ConversationHistory);
        Assert.Equal(original.Data.Session!.Value.GetRawText(), written.Data.Session!.Value.GetRawText());
        Assert.Equal(original.Data.HistoryBinding.GetRawText(), written.Data.HistoryBinding.GetRawText());
        Assert.Equal(DurableAgentStateCompletionReceipt.FailedOutcome, written.Data.CompletionReceipts!["failed"].Outcome);
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
}
