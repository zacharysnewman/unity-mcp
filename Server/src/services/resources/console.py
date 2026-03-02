"""
MCP Resource for reading full details of a Unity Editor console log entry.
"""
from typing import Any

from fastmcp import Context

from models import MCPResponse
from services.registry import mcp_for_unity_resource
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


def _normalize_response(response: dict | Any) -> MCPResponse:
    if isinstance(response, dict):
        return MCPResponse(**response)
    return response


@mcp_for_unity_resource(
    uri="mcpforunity://console/log/{log_id}",
    name="console_log",
    description=(
        "Get full details for a specific deduplicated console log entry by its ID. "
        "Returns the complete message (including stack trace), source file, line number, and occurrence count. "
        "Use read_console to list log entries and obtain IDs.\n\n"
        "URI: mcpforunity://console/log/{log_id}"
    ),
)
async def get_console_log(ctx: Context, log_id: str) -> MCPResponse:
    """Get full details for a console log entry by ID."""
    unity_instance = get_unity_instance_from_context(ctx)
    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "get_console_log",
        {"id": log_id}
    )
    return _normalize_response(response)
