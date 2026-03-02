"""
Defines the read_console tool for accessing Unity Editor console messages.
"""
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.utils import coerce_int, coerce_bool
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    description=(
        "Gets deduplicated messages from or clears the Unity Editor console. "
        "Returns unique log entries with occurrence counts. Defaults to errors only. "
        "Use mcpforunity://console/log/{id} to get the full message and stack trace for a specific entry."
    ),
    annotations=ToolAnnotations(
        title="Read Console",
    ),
)
async def read_console(
    ctx: Context,
    action: Annotated[Literal['get', 'clear'],
                      "Get or clear the Unity Editor console. Defaults to 'get'."] | None = None,
    include_errors: Annotated[bool | str,
                              "Include error and exception entries (default: true)"] | None = None,
    include_warnings: Annotated[bool | str,
                                "Include warning entries (default: false)"] | None = None,
    include_logs: Annotated[bool | str,
                            "Include log/info entries (default: false)"] | None = None,
    filter_text: Annotated[str, "Optional case-insensitive substring filter"] | None = None,
    page_size: Annotated[int | str, "Number of unique entries per page (default: 50)"] | None = None,
    cursor: Annotated[int | str, "Pagination cursor (0-based offset, default: 0)"] | None = None,
) -> dict[str, Any]:
    unity_instance = get_unity_instance_from_context(ctx)
    action = (action or 'get').lower()

    params_dict: dict[str, Any] = {"action": action}

    if action == 'get':
        params_dict.update({
            "includeErrors": coerce_bool(include_errors, default=True),
            "includeWarnings": coerce_bool(include_warnings, default=False),
            "includeLogs": coerce_bool(include_logs, default=False),
            "pageSize": coerce_int(page_size, default=50),
            "cursor": coerce_int(cursor, default=0),
        })
        if filter_text is not None:
            params_dict["filterText"] = filter_text

    resp = await send_with_unity_instance(
        async_send_command_with_retry, unity_instance, "read_console", params_dict
    )
    return resp if isinstance(resp, dict) else {"success": False, "message": str(resp)}
