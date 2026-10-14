// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics.CodeAnalysis;
using DurableTask.Core.Entities;
using DurableTask.Core.Entities.OperationFormat;
using Microsoft.Agents.AI.DurableTask.State;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.Shims;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Microsoft.Agents.AI.DurableTask.Tests.Unit;

public sealed partial class AgentEntityPublicRetentionTests
{
    private const string PrivateMarker = "untrusted-provider-diagnostic";
    private static readonly DateTimeOffset s_now = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly EntityId s_entityId = new(AgentSessionId.ToEntityName("agent"), "session");
    private static readonly DurableDataConverter s_converter = new();

    private static ChatClientAgent CreateAgent(ProbeClient client, ProbeProvider provider) =>
        new(client, new ChatClientAgentOptions { Name = "agent", ChatHistoryProvider = provider });

    private static async Task<EntityBatchResult> DispatchAsync(
        AIAgent agent,
        string? state,
        string correlation,
        bool mailbox = true,
        bool emptyInput = false,
        string operationName = nameof(AgentEntity.Run),
        DateTimeOffset? now = null,
        string? failBoundary = null,
        string? providerKey = "probe.v1",
        Action<DurableAgentsOptions>? configureOptions = null,
        string requestText = "request",
        IReadOnlyList<OperationRequest>? operations = null,
        DurableAgentHistoryReplayMode replayMode = DurableAgentHistoryReplayMode.PreloadEntityHistory)
    {
        DurableAgentsOptions options = new()
        {
            EnablePersistentRequestOutcomes = mailbox,
            DefaultTimeToLive = null,
            ResultRetentionPeriod = TimeSpan.FromMinutes(1),
        };
        configureOptions?.Invoke(options);
        options.AddAIAgent(agent, timeToLive: null, configureHistory: history =>
        {
            history.ProviderKey = providerKey is null ? null : new(providerKey);
            history.ReplayMode = replayMode;
        });
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton(options)
            .AddSingleton(options.GetAgentFactories())
            .AddSingleton(new Mock<DurableTaskClient>("test").Object)
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton<TimeProvider>(new Clock(now))
            .AddSingleton(Mock.Of<IHostApplicationLifetime>())
            .BuildServiceProvider();
        ITaskEntity entity = new AgentEntity(services);
        if (failBoundary == "afterSuccessfulDispatch")
        {
            entity = new FailAfterPublicDispatch(entity, correlation);
        }
        DurableTaskShimFactory factory = new(
            new DurableTaskWorkerOptions { DataConverter = new FailingConverter(failBoundary) },
            NullLoggerFactory.Instance);
        var shim = factory.CreateEntity(new(s_entityId.Name), entity, s_entityId);
        return await shim.ExecuteOperationBatchAsync(new EntityBatchRequest
        {
            InstanceId = s_entityId.ToString(),
            EntityState = state,
            Operations = operations?.ToList() ??
            [
                new OperationRequest
                {
                    Id = Guid.NewGuid(),
                    Operation = operationName,
                    Input = operationName == nameof(AgentEntity.Run)
                        ? s_converter.Serialize(new RunRequest(emptyInput ? [] : [new ChatMessage(ChatRole.User, requestText)])
                        {
                            CorrelationId = correlation,
                        })
                        : null,
                },
            ],
        });
    }

    private static DurableAgentState ReadState(EntityBatchResult batch) =>
        Assert.IsType<DurableAgentState>(s_converter.Deserialize(batch.EntityState, typeof(DurableAgentState)));

    private static OperationResult Result(EntityBatchResult batch) =>
        Assert.Single(Assert.IsType<List<OperationResult>>(batch.Results));

    private static global::DurableTask.Core.FailureDetails Failure(EntityBatchResult batch) =>
        Assert.IsType<global::DurableTask.Core.FailureDetails>(Result(batch).FailureDetails);

    private static List<OperationAction> Actions(EntityBatchResult batch) =>
        Assert.IsType<List<OperationAction>>(batch.Actions);

    private sealed class Clock(DateTimeOffset? now = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now ?? s_now;
    }

    private sealed class ProbeProvider : ChatHistoryProvider
    {
        public override IReadOnlyList<string> StateKeys => ["continuation"];
        public string? Stage { get; set; }
        public Exception? Failure { get; set; }
        public int LoadCount { get; private set; }
        public int StoreCount { get; private set; }
        public int? RestoredVersion { get; private set; }
        public List<string> RemoteMessages { get; init; } = [];

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            this.LoadCount++;
            if (context.Session!.StateBag.TryGetValue("continuation", out Continuation? value))
            {
                this.RestoredVersion = value?.Version;
            }
            if (this.Stage == "load")
            {
                this.ThrowIfFailed(context.Session);
            }
            return new(this.Stage == "lazyLoad" ? this.LazyFailure(context.Session) : []);
        }

        private IEnumerable<ChatMessage> LazyFailure(AgentSession session)
        {
            this.ThrowIfFailed(session);
            yield break;
        }

        protected override ValueTask StoreChatHistoryAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
        {
            this.StoreCount++;
            if (this.Stage == "storeBefore")
            {
                this.ThrowIfFailed(context.Session!);
            }
            this.RemoteMessages.AddRange(context.RequestMessages.Select(message => message.Text ?? string.Empty));
            context.Session!.StateBag.SetValue("continuation", new Continuation { Version = this.StoreCount });
            if (this.Stage == "storeAfter")
            {
                this.ThrowIfFailed(context.Session);
            }
            return default;
        }

        private void ThrowIfFailed(AgentSession session)
        {
            if (this.Failure is not null)
            {
                session.StateBag.SetValue("continuation", new Continuation { Version = 999 });
                session.StateBag.SetValue("partial-state", PrivateMarker);
                throw this.Failure;
            }
        }
    }

    private sealed class Continuation
    {
        public int Version { get; set; }
    }

    private sealed class ProbeClient : IChatClient
    {
        public int Count { get; private set; }
        public List<ChatMessage> LastMessages { get; private set; } = [];
        public Exception? Failure { get; init; }
        public string? ResponseConversationId { get; init; }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.Count++;
            this.LastMessages = messages.ToList();
            await Task.Yield();
            if (this.Failure is not null)
            {
                throw this.Failure;
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "response")
            {
                ConversationId = this.ResponseConversationId,
            };
        }
    }

    private sealed class CommitFailureException : Exception
    {
        public CommitFailureException() { }
        public CommitFailureException(string? message) : base(message) { }
        public CommitFailureException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    private sealed class ToolLoopClient(string toolName) : IChatClient
    {
        public int Count { get; private set; }
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.Count++;
            await Task.Yield();
            if (this.Count == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", toolName)]);
                yield break;
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "final response");
        }
    }

    private sealed class FailingConverter(string? boundary) : DataConverter
    {
        public override object? Deserialize(string? data, Type targetType) => s_converter.Deserialize(data, targetType);

        [return: NotNullIfNotNull(nameof(value))]
        public override string? Serialize(object? value)
        {
            if ((boundary == "state" && value is DurableAgentState) ||
                (boundary == "response" && value is AgentResponse))
            {
                throw new CommitFailureException();
            }
            return s_converter.Serialize(value);
        }
    }
}
