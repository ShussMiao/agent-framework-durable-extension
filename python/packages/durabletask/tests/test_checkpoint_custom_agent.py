# Copyright (c) Microsoft. All rights reserved.

"""Native entity-boundary regression for checkpoint replay into custom agents."""

from __future__ import annotations

import json
from collections.abc import AsyncIterator, Sequence
from typing import Any
from unittest.mock import Mock

import pytest
from agent_framework import AgentResponse, AgentResponseUpdate, Message, ResponseStream, SupportsAgentRun
from durabletask.internal import helpers
from durabletask.internal import orchestrator_service_pb2 as pb
from durabletask.task import EntityContext
from durabletask.worker import TaskHubGrpcWorker

from agent_framework_durabletask._checkpoints._agent import create_entity

_ENTITY_ADDRESS = "@dafx-checkpoint-custom-agent@session-1"


class _SlottedCustomAgent:
    __slots__ = ("id", "name", "description", "calls")

    def __init__(self) -> None:
        self.id = "slotted-custom"
        self.name = "slotted-custom"
        self.description = "Custom provider without Agent middleware support"
        self.calls: list[tuple[bool, list[Message]]] = []

    def run(self, messages: Any = None, *, stream: bool = False, **kwargs: Any) -> Any:
        transcript = list(messages or [])
        self.calls.append((stream, transcript))

        if stream:
            if any(content.text == "second prompt" for message in transcript for content in message.contents):
                raise TypeError("stream is not supported")

            async def updates() -> AsyncIterator[AgentResponseUpdate]:
                if False:
                    yield AgentResponseUpdate()

            return ResponseStream(
                updates(),
                finalizer=lambda _: AgentResponse(messages=[Message("assistant", ["first answer"])]),
            )

        async def response() -> AgentResponse[Any]:
            return AgentResponse(messages=[Message("assistant", ["second answer"])])

        return response()

    def create_session(self, *, session_id: str | None = None) -> Any:
        raise NotImplementedError

    def get_session(self, service_session_id: Any, *, session_id: str | None = None) -> Any:
        raise NotImplementedError


def _run_native_operation(
    worker: TaskHubGrpcWorker,
    *,
    request_id: str,
    message: str,
    state: str | None,
) -> Any:
    request = pb.EntityBatchRequest(
        instanceId=_ENTITY_ADDRESS,
        entityState=helpers.get_string_value(state),
        operations=[
            pb.OperationRequest(
                operation="run",
                requestId=request_id,
                input=helpers.get_string_value(
                    json.dumps({"message": message, "correlationId": "same-public-correlation"})
                ),
            )
        ],
    )
    return worker._execute_entity_batch(request, Mock(), "checkpoint-custom-test")


def _texts(messages: Sequence[Message]) -> list[list[str | None]]:
    return [[content.text for content in message.contents] for message in messages]


def test_native_entity_replay_preserves_slotted_agent_transcript_and_request_identity() -> None:
    if not hasattr(EntityContext, "operation_id"):
        pytest.skip("Requires the proposed SDK entity operation identity API.")
    agent = _SlottedCustomAgent()
    assert isinstance(agent, SupportsAgentRun)
    entity_type = create_entity(agent, entity_id="checkpoint-custom-agent")
    worker = TaskHubGrpcWorker(channel=Mock(), emit_trace_spans=False)
    worker._registry.add_entity(entity_type)

    first = _run_native_operation(
        worker,
        request_id="native-first",
        message="first prompt",
        state=None,
    )
    assert first.results[0].HasField("success"), first
    assert _texts(agent.calls[0][1]) == [["first prompt"]]

    second = _run_native_operation(
        worker,
        request_id="native-second",
        message="second prompt",
        state=first.entityState.value,
    )
    assert second.results[0].HasField("success"), second
    expected_transcript = [["first prompt"], ["first answer"], ["second prompt"]]
    assert _texts(agent.calls[1][1]) == expected_transcript
    assert _texts(agent.calls[2][1]) == expected_transcript
    assert [stream for stream, _ in agent.calls] == [True, True, False]

    checkpoint = json.loads(second.entityState.value)
    assert set(checkpoint["nativeOperations"]) == {"native-first", "native-second"}
    assert [row["nativeOperationId"] for row in checkpoint["observations"]] == [
        "native-first",
        "native-second",
    ]
    assert checkpoint["observations"][0]["correlation"] == checkpoint["observations"][1]["correlation"]

    redelivery = _run_native_operation(
        worker,
        request_id="native-first",
        message="first prompt",
        state=second.entityState.value,
    )
    assert redelivery.results[0].HasField("success"), redelivery
    assert redelivery.results[0].success.result.value == first.results[0].success.result.value
    assert len(agent.calls) == 3
