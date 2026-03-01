import pytest

from .test_helpers import DummyContext
import services.resources.menu_items as menu_items_mod


@pytest.mark.asyncio
async def test_get_menu_items_default_params(monkeypatch):
    """Default call sends refresh=False, empty search, page_size=50, cursor=0."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["cmd"] = cmd
        captured["params"] = params
        return {
            "success": True,
            "data": {
                "items": ["Assets/Create/Material", "Edit/Copy"],
                "total": 2,
                "cursor": 0,
                "pageSize": 50,
                "next_cursor": None,
                "truncated": False,
            },
        }

    monkeypatch.setattr(menu_items_mod, "async_send_command_with_retry", fake_send)

    resp = await menu_items_mod.get_menu_items(ctx=DummyContext())

    assert resp.success is True
    p = captured["params"]
    assert p["refresh"] is False
    assert p["search"] == ""
    assert p["pageSize"] == 50
    assert p["cursor"] == 0


@pytest.mark.asyncio
async def test_get_menu_items_search_param(monkeypatch):
    """Search string is forwarded correctly."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {
            "success": True,
            "data": {
                "items": ["Assets/Create/Material"],
                "total": 1,
                "cursor": 0,
                "pageSize": 50,
                "next_cursor": None,
                "truncated": False,
            },
        }

    monkeypatch.setattr(menu_items_mod, "async_send_command_with_retry", fake_send)

    resp = await menu_items_mod.get_menu_items(ctx=DummyContext(), search="Material")

    assert resp.success is True
    assert captured["params"]["search"] == "Material"


@pytest.mark.asyncio
async def test_get_menu_items_pagination_params(monkeypatch):
    """page_size and cursor are forwarded correctly."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {
            "success": True,
            "data": {
                "items": ["Edit/Copy", "Edit/Paste"],
                "total": 100,
                "cursor": 10,
                "pageSize": 2,
                "next_cursor": "12",
                "truncated": True,
            },
        }

    monkeypatch.setattr(menu_items_mod, "async_send_command_with_retry", fake_send)

    resp = await menu_items_mod.get_menu_items(ctx=DummyContext(), page_size=2, cursor=10)

    assert resp.success is True
    p = captured["params"]
    assert p["pageSize"] == 2
    assert p["cursor"] == 10
    assert resp.data is not None
    assert resp.data.truncated is True
    assert resp.data.next_cursor == "12"
    assert resp.data.total == 100


@pytest.mark.asyncio
async def test_get_menu_items_error_response(monkeypatch):
    """Unity error response propagates as success=False."""

    async def fake_send(cmd, params, **kwargs):
        return {"success": False, "message": "Unity not ready"}

    monkeypatch.setattr(menu_items_mod, "async_send_command_with_retry", fake_send)

    resp = await menu_items_mod.get_menu_items(ctx=DummyContext())

    assert resp.success is False
    assert "Unity not ready" in (resp.message or "")
