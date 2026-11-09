# MCP server: read-only, small tools over the state Unity sent (state.json).
import json
from pathlib import Path

from mcp.server.fastmcp import FastMCP

STATE_FILE = Path(__file__).with_name("state.json")
mcp = FastMCP("npc-decision")


def load() -> dict:
    return json.loads(STATE_FILE.read_text(encoding="utf-8"))


@mcp.tool()
def get_npc_objective() -> dict:
    """Returns the NPC's role (Guard, Scout or Assault) and its tactical goal. The decision should serve this goal."""
    return load()["objective"]


@mcp.tool()
def get_npc_state() -> dict:
    """Returns the NPC's current state: health (0-100), ammo and maxAmmo, distance to the nearest visible
    enemy in metres (-1 if none), number of visible enemies, allies within 10 m, allies that can still be
    called for help, whether cover is within reach, and the action it is currently executing."""
    return load()["npc"]


@mcp.tool()
def get_legal_actions() -> list[str]:
    """Returns the tactical actions the game allows right now. The final decision MUST be one of these."""
    return load()["legalActions"]


@mcp.tool()
def get_environment_context() -> dict:
    """Returns tactical context: distance to the nearest cover in metres (-1 if none), whether the NPC is
    already behind cover that blocks the nearest enemy, whether it was hit in the last 3 seconds, and the
    NPC's and the enemies' weapon ranges in metres."""
    return load()["environment"]


if __name__ == "__main__":
    mcp.run()  # stdio transport
