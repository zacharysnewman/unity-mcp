import pytest

from .test_helpers import DummyContext, DummyMCP


def setup_console_tools():
    """Setup console-related tools for testing."""
    mcp = DummyMCP()
    import services.tools.read_console
    from services.registry import get_registered_tools
    for tool_info in get_registered_tools():
        tool_name = tool_info['name']
        if 'read_console' in tool_name or 'console' in tool_name:
            mcp.tools[tool_name] = tool_info['func']
    return mcp.tools


def _make_fake_send(captured, items=None, total_count=None, next_cursor=None):
    """Helper to create a fake Unity send function returning paginated results."""
    items = items or []
    total_count = total_count if total_count is not None else len(items)

    async def fake_send(_cmd, params, **_kwargs):
        captured["params"] = params
        return {
            "success": True,
            "data": {
                "items": items,
                "cursor": params.get("cursor", 0),
                "pageSize": params.get("pageSize", 50),
                "nextCursor": next_cursor,
                "totalCount": total_count,
                "hasMore": next_cursor is not None,
            },
        }

    return fake_send


@pytest.mark.asyncio
async def test_read_console_defaults(monkeypatch):
    """Default call sends includeErrors=True, warnings/logs=False, paginated at 50."""
    tools = setup_console_tools()
    read_console = tools["read_console"]
    captured = {}

    import services.tools.read_console as mod
    monkeypatch.setattr(mod, "async_send_command_with_retry", _make_fake_send(captured))

    resp = await read_console(ctx=DummyContext())
    assert resp["success"] is True
    assert captured["params"]["action"] == "get"
    assert captured["params"]["includeErrors"] is True
    assert captured["params"]["includeWarnings"] is False
    assert captured["params"]["includeLogs"] is False
    assert captured["params"]["pageSize"] == 50
    assert captured["params"]["cursor"] == 0


@pytest.mark.asyncio
async def test_read_console_include_warnings_and_logs(monkeypatch):
    """Can opt in to warnings and logs, and opt out of errors."""
    tools = setup_console_tools()
    read_console = tools["read_console"]
    captured = {}

    import services.tools.read_console as mod
    monkeypatch.setattr(mod, "async_send_command_with_retry", _make_fake_send(captured))

    await read_console(ctx=DummyContext(), include_errors=False, include_warnings=True, include_logs=True)
    assert captured["params"]["includeErrors"] is False
    assert captured["params"]["includeWarnings"] is True
    assert captured["params"]["includeLogs"] is True


@pytest.mark.asyncio
async def test_read_console_pagination(monkeypatch):
    """page_size and cursor are forwarded correctly; nextCursor/hasMore pass through."""
    tools = setup_console_tools()
    read_console = tools["read_console"]
    captured = {}

    import services.tools.read_console as mod
    monkeypatch.setattr(mod, "async_send_command_with_retry", _make_fake_send(captured, next_cursor=10, total_count=25))

    resp = await read_console(ctx=DummyContext(), page_size=10, cursor=0)
    assert resp["success"] is True
    assert captured["params"]["pageSize"] == 10
    assert captured["params"]["cursor"] == 0
    assert resp["data"]["hasMore"] is True
    assert resp["data"]["nextCursor"] == 10
    assert resp["data"]["totalCount"] == 25


@pytest.mark.asyncio
async def test_read_console_filter_text(monkeypatch):
    """filter_text is forwarded when provided."""
    tools = setup_console_tools()
    read_console = tools["read_console"]
    captured = {}

    import services.tools.read_console as mod
    monkeypatch.setattr(mod, "async_send_command_with_retry", _make_fake_send(captured))

    await read_console(ctx=DummyContext(), filter_text="NullRef")
    assert captured["params"]["filterText"] == "NullRef"


@pytest.mark.asyncio
async def test_read_console_no_filter_text_by_default(monkeypatch):
    """filterText is not sent when not specified."""
    tools = setup_console_tools()
    read_console = tools["read_console"]
    captured = {}

    import services.tools.read_console as mod
    monkeypatch.setattr(mod, "async_send_command_with_retry", _make_fake_send(captured))

    await read_console(ctx=DummyContext())
    assert "filterText" not in captured["params"]


@pytest.mark.asyncio
async def test_read_console_clear(monkeypatch):
    """clear action sends action=clear and no filter/paging params."""
    tools = setup_console_tools()
    read_console = tools["read_console"]
    captured = {}

    async def fake_send(_cmd, params, **_kwargs):
        captured["params"] = params
        return {"success": True, "data": None}

    import services.tools.read_console as mod
    monkeypatch.setattr(mod, "async_send_command_with_retry", fake_send)

    resp = await read_console(ctx=DummyContext(), action="clear")
    assert resp["success"] is True
    assert captured["params"]["action"] == "clear"
    assert "includeErrors" not in captured["params"]
    assert "pageSize" not in captured["params"]


@pytest.mark.asyncio
async def test_read_console_response_passthrough(monkeypatch):
    """Deduplicated items with occurrence counts pass through unchanged."""
    tools = setup_console_tools()
    read_console = tools["read_console"]
    captured = {}

    items = [
        {"id": "a1b2c3d4", "type": "Error", "message": "NullReferenceException", "occurrenceCount": 5},
        {"id": "e5f6a7b8", "type": "Error", "message": "IndexOutOfRange", "occurrenceCount": 1},
    ]

    import services.tools.read_console as mod
    monkeypatch.setattr(mod, "async_send_command_with_retry", _make_fake_send(captured, items=items, total_count=2))

    resp = await read_console(ctx=DummyContext())
    assert resp["success"] is True
    assert len(resp["data"]["items"]) == 2
    assert resp["data"]["items"][0]["occurrenceCount"] == 5
    assert resp["data"]["items"][0]["id"] == "a1b2c3d4"
