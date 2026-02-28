"""Script CLI commands."""

import os
import click
from typing import Any

from cli.utils.config import get_config
from cli.utils.output import format_output
from cli.utils.connection import run_command, handle_unity_errors


@click.group()
def script():
    """Script operations."""
    pass


@script.command("validate")
@click.argument("path")
@click.option(
    "--level", "-l",
    type=click.Choice(["basic", "standard"]),
    default="basic",
    help="Validation level."
)
@handle_unity_errors
def validate(path: str, level: str):
    """Validate a C# script for errors.

    \b
    Examples:
        unity-mcp script validate "Assets/Scripts/Player.cs"
        unity-mcp script validate "Assets/Scripts/Player.cs" --level standard
    """
    config = get_config()

    parts = path.replace("\\", "/").split("/")
    filename = os.path.splitext(parts[-1])[0]
    directory = "/".join(parts[:-1]) or "Assets"

    params: dict[str, Any] = {
        "action": "validate",
        "name": filename,
        "path": directory,
        "level": level,
    }

    result = run_command("manage_script", params, config)
    click.echo(format_output(result, config.format))
