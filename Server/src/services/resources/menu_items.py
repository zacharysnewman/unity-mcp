from pydantic import BaseModel
from fastmcp import Context

from models import MCPResponse
from models.unity_response import parse_resource_response
from services.registry import mcp_for_unity_resource
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


class MenuItemsPage(BaseModel):
    items: list[str] = []
    total: int = 0
    cursor: int = 0
    pageSize: int = 50
    next_cursor: str | None = None
    truncated: bool = False


class GetMenuItemsResponse(MCPResponse):
    data: MenuItemsPage | None = None


@mcp_for_unity_resource(
    uri="mcpforunity://menu-items",
    name="menu_items",
    description=(
        "Paginated list of Unity menu items. "
        "Use search to filter by substring, page_size and cursor for pagination. "
        "Returns items, total, cursor, pageSize, next_cursor, truncated.\n\n"
        "URI: mcpforunity://menu-items"
    )
)
async def get_menu_items(
    ctx: Context,
    search: str = "",
    page_size: int = 50,
    cursor: int = 0,
) -> GetMenuItemsResponse | MCPResponse:
    """Provides a paginated list of Unity menu items."""
    unity_instance = get_unity_instance_from_context(ctx)
    params = {
        "refresh": False,
        "search": search,
        "pageSize": page_size,
        "cursor": cursor,
    }

    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "get_menu_items",
        params,
    )
    return parse_resource_response(response, GetMenuItemsResponse)
