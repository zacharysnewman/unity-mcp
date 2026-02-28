import os
from typing import Annotated, Any, Literal
from urllib.parse import urlparse, unquote

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
import transport.legacy.unity_connection


def _split_uri(uri: str) -> tuple[str, str]:
    """Split an incoming URI or path into (name, directory) suitable for Unity."""
    raw_path: str
    if uri.startswith("mcpforunity://path/"):
        raw_path = uri[len("mcpforunity://path/"):]
    elif uri.startswith("file://"):
        parsed = urlparse(uri)
        host = (parsed.netloc or "").strip()
        p = parsed.path or ""
        if host and host.lower() != "localhost":
            p = f"//{host}{p}"
        raw_path = unquote(p)
    else:
        raw_path = uri

    raw_path = unquote(raw_path).replace("\\", "/")
    if os.name == "nt" and len(raw_path) >= 3 and raw_path[0] == "/" and raw_path[2] == ":":
        raw_path = raw_path[1:]

    norm = os.path.normpath(raw_path).replace("\\", "/")
    parts = [p for p in norm.split("/") if p not in ("", ".")]
    idx = next((i for i, seg in enumerate(parts) if seg.lower() == "assets"), None)
    assets_rel = "/".join(parts[idx:]) if idx is not None else None

    effective_path = assets_rel if assets_rel else norm
    if effective_path.startswith("/"):
        effective_path = effective_path[1:]

    name = os.path.splitext(os.path.basename(effective_path))[0]
    directory = os.path.dirname(effective_path)
    return name, directory


@mcp_for_unity_tool(
    unity_target="manage_script",
    description="Validate a C# script and return diagnostics.",
    annotations=ToolAnnotations(
        title="Validate Script",
        readOnlyHint=True,
    ),
)
async def validate_script(
    ctx: Context,
    uri: Annotated[str, "URI of the script to validate under Assets/ directory, mcpforunity://path/Assets/... or file://... or Assets/..."],
    level: Annotated[Literal['basic', 'standard'],
                     "Validation level"] = "basic",
    include_diagnostics: Annotated[bool,
                                   "Include full diagnostics and summary"] = False,
) -> dict[str, Any]:
    unity_instance = get_unity_instance_from_context(ctx)
    await ctx.info(
        f"Processing validate_script: {uri} (unity_instance={unity_instance or 'default'})")
    name, directory = _split_uri(uri)
    if not directory or directory.split("/")[0].lower() != "assets":
        return {"success": False, "code": "path_outside_assets", "message": "URI must resolve under 'Assets/'."}
    if level not in ("basic", "standard"):
        return {"success": False, "code": "bad_level", "message": "level must be 'basic' or 'standard'."}
    params = {
        "action": "validate",
        "name": name,
        "path": directory,
        "level": level,
    }
    resp = await send_with_unity_instance(
        transport.legacy.unity_connection.async_send_command_with_retry,
        unity_instance,
        "manage_script",
        params,
    )
    if isinstance(resp, dict) and resp.get("success"):
        diags = resp.get("data", {}).get("diagnostics", []) or []
        warnings = sum(1 for d in diags if str(
            d.get("severity", "")).lower() == "warning")
        errors = sum(1 for d in diags if str(
            d.get("severity", "")).lower() in ("error", "fatal"))
        if include_diagnostics:
            return {"success": True, "data": {"diagnostics": diags, "summary": {"warnings": warnings, "errors": errors}}}
        return {"success": True, "data": {"warnings": warnings, "errors": errors}}
    return resp if isinstance(resp, dict) else {"success": False, "message": str(resp)}
