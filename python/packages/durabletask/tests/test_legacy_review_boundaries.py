# Copyright (c) Microsoft. All rights reserved.

"""Legacy-path controls for the published prototype review findings."""

from __future__ import annotations

from copy import deepcopy
from typing import Any

import pytest
from _execution_test_support import JsonStateProvider, RecordingChatClient
from _history_pipeline_test_support import ToolChatClient
from _invocation_test_support import lookup
from agent_framework import Agent, Message
from test_agent_compatibility_prototype import _evidence, _legacy
from test_complete_compatibility import _continued, _ExternalHistory

from agent_framework_durabletask import (
    CompatibleAgentEntity,
    LegacyMigrationEvidence,
    RunRequest,
    state_snapshot_digest,
)
from agent_framework_durabletask._shared_response import serialize_terminal_response


class _ToolPolicyAgent(Agent):
    def __init__(self, **kwargs: Any) -> None:
        super().__init__(**kwargs)
        self.invocations: list[tuple[dict[str, Any], Any]] = []

    def run(self, *args: Any, **kwargs: Any) -> Any:
        configuration = getattr(self.client, "function_invocation_configuration", {})
        self.invocations.append((dict(kwargs.get("options", {})), configuration.get("enabled")))
        return super().run(*args, **kwargs)


@pytest.mark.asyncio
@pytest.mark.parametrize("migrate", [False, True])
@pytest.mark.parametrize("replacement", ["same", "different", "entity"])
async def test_committed_legacy_owner_applies_before_both_execution_paths(
    migrate: bool,
    replacement: str,
) -> None:
    source = _continued()
    source["data"]["extensionData"]["_dafx_legacy_owner"] = {
        "profile": "dafx-legacy-owner-v1",
        "owner": "external",
        "sourceId": "external",
    }
    external = _ExternalHistory(source_id="different" if replacement == "different" else "external")
    client = RecordingChatClient()
    provider = JsonStateProvider(source)
    agent = Agent(client=client, name="owner", context_providers=[] if replacement == "entity" else [external])
    entity = CompatibleAgentEntity(
        agent,
        state_provider=provider,
        legacy_migration_evidence=_evidence if migrate else None,
    )
    if replacement != "same":
        with pytest.raises(ValueError, match="ownership"):
            await entity.run(RunRequest("question", correlation_id="next"))
        assert provider.raw == source and client.received_messages == [] and external.ids == []
    else:
        await entity.run(RunRequest("question", correlation_id="next"))
        assert external.ids == ["original-logical-session"]
        assert provider.raw["schemaVersion"] == ("2.0.0" if migrate else "1.1.0")


@pytest.mark.asyncio
@pytest.mark.parametrize("enabled", [False, True])
async def test_legacy_tool_policy_is_invocation_local_and_controls_advertisement(enabled: bool) -> None:
    client = ToolChatClient(tool_calls=False)
    agent = _ToolPolicyAgent(client=client, name="tools", tools=[lookup], default_options={"tool_choice": "auto"})
    defaults = dict(agent.default_options)
    configuration = client.function_invocation_configuration
    before = deepcopy(configuration)
    entity = CompatibleAgentEntity(agent, state_provider=JsonStateProvider(_legacy()), deployment_mode="legacy")
    await entity.run(
        RunRequest(
            "question",
            correlation_id="next",
            enable_tool_calls=enabled,
            options={"tools": [lookup], "tool_choice": "required"},
        )
    )
    options = client.received_options[0]
    if enabled:
        assert options["tools"] and options["tool_choice"] == "required"
    else:
        invocation, invocation_enabled = agent.invocations[0]
        assert invocation["tools"] == [] and invocation["tool_choice"] == "none" and invocation_enabled is False
        assert not options.get("tools") and options["tool_choice"] == "none"
    assert agent.default_options == defaults
    assert agent.client is client
    assert client.function_invocation_configuration is configuration and configuration == before
    await entity.run(RunRequest("enabled again", correlation_id="later"))
    assert client.received_options[-1]["tools"]


@pytest.mark.asyncio
async def test_legacy_disabled_tools_do_not_advertise_service_hosted_definitions() -> None:
    client = RecordingChatClient()
    hosted: Any = {"type": "web_search"}
    agent = _ToolPolicyAgent(client=client, name="hosted", tools=[hosted])
    entity = CompatibleAgentEntity(agent, state_provider=JsonStateProvider(_legacy()), deployment_mode="legacy")
    await entity.run(
        RunRequest(
            "question",
            correlation_id="next",
            enable_tool_calls=False,
            options={"tools": [hosted], "tool_choice": "auto"},
        )
    )
    assert agent.invocations[0][0]["tools"] == []
    assert not client.received_options[0].get("tools")
    assert client.received_options[0]["tool_choice"] == "none"
    assert agent.default_options["tools"]


def _delivery_only(source: dict[str, Any]) -> LegacyMigrationEvidence:
    return LegacyMigrationEvidence(
        delivery_evidence={
            "sourceDigest": state_snapshot_digest(source),
            "evidenceId": "recorded-public-lookups",
            "complete": True,
            "messages": [Message("user", ["accepted"], message_id="accepted-id").to_dict()],
            "messagePositions": [{"producer": "upstream", "position": 0}],
        }
    )


@pytest.mark.asyncio
@pytest.mark.parametrize("first_explicit", [False, True])
@pytest.mark.parametrize("second_explicit", [False, True])
async def test_scalar_public_receipts_remain_distinct_from_actual_legacy_occurrences(
    first_explicit: bool,
    second_explicit: bool,
) -> None:
    source = _legacy()
    source["data"]["ingestedPositions"] = {"upstream": 0}
    provider = JsonStateProvider(source)
    first = CompatibleAgentEntity(
        Agent(client=RecordingChatClient(), name="delivery"),
        state_provider=provider,
        legacy_migration_evidence=_delivery_only,
    )

    def request(correlation: str, explicit: bool) -> RunRequest:
        return RunRequest(
            "",
            correlation_id=correlation,
            context_messages=[Message("user", ["accepted"], message_id="accepted-id").to_dict()],
            context_message_ids=["accepted-id"] if explicit else None,
        )

    await first.run(request("first", first_explicit))
    receipts = provider.raw["data"]["extensionData"]["_dafx_legacy_ingestion"]["messages"]
    assert ("accepted-id" in receipts) is first_explicit
    client = RecordingChatClient()
    cold_provider = JsonStateProvider(provider.raw)
    cold = CompatibleAgentEntity(
        Agent(client=client, name="delivery"),
        state_provider=cold_provider,
        legacy_migration_evidence=_delivery_only,
    )
    await cold.run(request("second", second_explicit))
    assert sum(message.text == "accepted" for message in client.received_messages[0]) == int(
        first_explicit or second_explicit
    )
    assert cold_provider.raw["data"]["ingestedPositions"] == {"upstream": 0}


@pytest.mark.asyncio
@pytest.mark.parametrize("version", ["v1", "v2"])
async def test_only_the_declared_public_lookup_profile_interprets_its_field(version: str) -> None:
    source = _legacy()
    profile = {
        "profile": f"dafx-legacy-ingestion-{version}",
        "messages": {},
        "publicMessages": {"business": 42},
    }
    source["data"]["extensionData"]["_dafx_legacy_ingestion"] = profile
    provider = JsonStateProvider(source)
    client = RecordingChatClient()
    entity = CompatibleAgentEntity(
        Agent(client=client, name="delivery"),
        state_provider=provider,
        deployment_mode="legacy",
    )
    request = RunRequest(
        "",
        correlation_id="next",
        context_messages=[Message("user", ["new"]).to_dict()],
        context_message_ids=["occurrence"],
    )
    if version == "v2":
        with pytest.raises(ValueError, match="exact content fingerprints"):
            await entity.run(request)
        assert provider.raw == source and client.received_messages == []
    else:
        await entity.run(request)
        assert provider.raw["data"]["extensionData"]["_dafx_legacy_ingestion"]["publicMessages"] == {"business": 42}
        assert any(message.text == "new" for message in client.received_messages[0])


@pytest.mark.asyncio
async def test_failed_legacy_delivery_does_not_commit_either_receipt_namespace() -> None:
    source = _legacy()
    source["data"]["ingestedPositions"] = {"upstream": 0}
    provider = JsonStateProvider(source)
    entity = CompatibleAgentEntity(
        Agent(client=RecordingChatClient(fail=True), name="delivery"),
        state_provider=provider,
        deployment_mode="legacy",
        legacy_migration_evidence=_delivery_only,
    )
    response = await entity.run(
        RunRequest(
            "",
            correlation_id="failed",
            context_messages=[Message("user", ["accepted"], message_id="accepted-id").to_dict()],
            context_message_ids=["independent"],
        )
    )
    assert response.additional_properties["durable_status"] == "error"
    assert provider.raw["data"]["extensionData"] == source["data"]["extensionData"]
    assert provider.raw["data"]["ingestedPositions"] == {"upstream": 0}


@pytest.mark.asyncio
async def test_legacy_public_journal_is_not_promoted_into_v2_occurrence_receipts() -> None:
    source = _legacy()
    source["data"]["ingestedPositions"] = {"upstream": 0}
    provider = JsonStateProvider(source)
    legacy = CompatibleAgentEntity(
        Agent(client=RecordingChatClient(), name="delivery"),
        state_provider=provider,
        deployment_mode="legacy",
        legacy_migration_evidence=_delivery_only,
    )
    response = await legacy.run(
        RunRequest(
            "",
            correlation_id="first",
            context_messages=[Message("user", ["accepted"], message_id="accepted-id").to_dict()],
        )
    )
    completed_at = provider.raw["data"]["conversationHistory"][-1]["createdAt"]

    def evidence(raw: dict[str, Any]) -> LegacyMigrationEvidence:
        completion = _evidence(raw).completion_evidence
        assert completion is not None
        completion["results"].append({
            "correlationId": "first",
            "outcome": "succeeded",
            "completedAt": completed_at,
            "response": serialize_terminal_response(response),
        })
        return LegacyMigrationEvidence(
            completion_evidence=completion,
            delivery_evidence=_delivery_only(raw).delivery_evidence,
        )

    converted = CompatibleAgentEntity(
        Agent(client=RecordingChatClient(), name="delivery"),
        state_provider=provider,
        legacy_migration_evidence=evidence,
    )
    await converted.run(RunRequest("convert", correlation_id="convert"))
    assert provider.raw["schemaVersion"] == "2.0.0"
    assert "accepted-id" not in provider.raw["data"].get("pythonIngestion", {}).get("messages", {})
    client = RecordingChatClient()
    cold = CompatibleAgentEntity(
        Agent(client=client, name="delivery"),
        state_provider=JsonStateProvider(provider.raw),
    )
    await cold.run(
        RunRequest(
            "",
            correlation_id="independent",
            context_messages=[Message("user", ["accepted"], message_id="accepted-id").to_dict()],
            context_message_ids=["accepted-id"],
        )
    )
    assert sum(message.text == "accepted" for message in client.received_messages[0]) == 1


@pytest.mark.asyncio
@pytest.mark.parametrize("identity", ["absent", "null"])
@pytest.mark.parametrize("owner", ["external", "service"])
async def test_legacy_continuation_without_saved_identity_uses_detached_fallback(
    identity: str,
    owner: str,
) -> None:
    source = _continued()
    if identity == "absent":
        source["data"]["session"].pop("session_id")
    else:
        source["data"]["session"]["session_id"] = None
    client = RecordingChatClient(conversation_id="next-remote")
    external = _ExternalHistory()
    if owner == "service":
        source["data"]["session"]["state"] = {}
        source["data"]["session"]["service_session_id"] = "existing-remote"
    provider = JsonStateProvider(source, session_id="physical")
    entity = CompatibleAgentEntity(
        Agent(client=client, name="continuation", context_providers=[external] if owner == "external" else []),
        state_provider=provider,
        deployment_mode="legacy",
    )
    await entity.run(
        RunRequest(
            "current",
            correlation_id="next",
            options={"store": owner == "service"},
        )
    )
    if owner == "external":
        assert external.ids == ["@runtime@physical"] and external.continuations == ["old"]
    else:
        assert [message.text for message in client.received_messages[0]] == ["current"]
    assert provider.raw["data"]["session"]["session_id"] == "@runtime@physical"
    assert provider.raw["data"]["session"]["futureSession"] == {"keep": True}
    assert source["data"]["session"].get("session_id") is None


@pytest.mark.asyncio
async def test_legacy_missing_identity_restoration_does_not_commit_on_provider_failure() -> None:
    source = _continued()
    source["data"]["session"].pop("session_id")
    provider = JsonStateProvider(source)
    external = _ExternalHistory(fail=True)
    entity = CompatibleAgentEntity(
        Agent(client=RecordingChatClient(), name="continuation", context_providers=[external]),
        state_provider=provider,
        deployment_mode="legacy",
    )
    response = await entity.run(RunRequest("current", correlation_id="failure"))
    assert response.additional_properties["durable_status"] == "error"
    assert external.continuations == ["old"]
    assert provider.raw["data"]["session"] == source["data"]["session"]


@pytest.mark.parametrize("external", [False, True])
def test_legacy_reset_respects_primary_history_ownership(external: bool) -> None:
    source = _continued() if external else _legacy()
    provider = JsonStateProvider(source)
    history = _ExternalHistory()
    entity = CompatibleAgentEntity(
        Agent(client=RecordingChatClient(), name="reset", context_providers=[history] if external else []),
        state_provider=provider,
        deployment_mode="legacy",
    )
    if external:
        with pytest.raises(NotImplementedError, match="provider-owned clear"):
            entity.reset()
        assert provider.raw == source and history.ids == []
    else:
        entity.reset()
        assert provider.raw["schemaVersion"] == "1.1.0"
        assert provider.raw["data"]["conversationHistory"] == []
