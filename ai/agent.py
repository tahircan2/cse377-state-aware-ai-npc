# Local AI agent: Unity -> POST /decide -> MCP tools -> Ollama -> structured decision.
#   python agent.py                         start the HTTP server Unity talks to (127.0.0.1:8000)
#   python agent.py test [A] [Scout ...]    run scenarios.json from the terminal, without Unity
#                                           (optional scenario names and roles; default: all scenarios, Guard)
import asyncio
import json
import os
import re
import sys
import time
from contextlib import asynccontextmanager
from pathlib import Path

import ollama
import uvicorn
from fastapi import FastAPI
from fastapi.responses import JSONResponse
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

# Ternary-weight 9B model: fits a 4 GB GPU and was the only tested model that chose reasonably in all of A-D.
MODEL = os.environ.get("NPC_MODEL", "bonsai-2:9b")
HERE = Path(__file__).parent
STATE_FILE = HERE / "state.json"
# The decision is never made without these; if the model skips one, the agent fetches it itself.
REQUIRED_TOOLS = ("get_npc_objective", "get_npc_state", "get_legal_actions", "get_environment_context")
DECISION_TIMEOUT = 7.8   # seconds; just under Unity's 8 s timeout, so the agent never keeps working for a request Unity dropped

SYSTEM = (
    "You are the tactical decision module of one NPC in a shooter game. The game executes the single action "
    "you pick; you never control movement and never invent actions.\n"
    "1. Call get_npc_objective, get_npc_state, get_legal_actions and get_environment_context, each once, "
    "all in the same turn, without writing text.\n"
    "2. Use only the values the tools return. Never assume anything that is not in the tool results.\n"
    "3. Choose exactly ONE action from the legal actions.\n"
    "Tactical knowledge to weigh together (these are considerations, not fixed rules; the goal breaks ties):\n"
    "- A healthy, armed NPC facing a single enemy is in a strong position.\n"
    "- Being outnumbered with no cover is very dangerous; avoid a stand-up fight you are likely to lose.\n"
    "- With low health, survival comes first.\n"
    "- An empty weapon cannot fight; reloading is safest behind cover or outside the enemies' weapon range.\n"
    "- Enemies outside their own weapon range cannot hurt you, while you may still be able to hit them.\n"
    "- Allies nearby make a fight safer; calling allies helps when a fight is hard.")
# Personalities for the terminal test (Unity sends its own, see GuardNpc.Roles). PDF §16.
ROLES = {
    "Guard": "Protect the gate and hold the area while staying alive.",
    "Scout": "Observe enemies and avoid unnecessary combat; survival matters more than kills.",
    "Assault": "Apply pressure on enemies while remaining combat-effective.",
}
# Reasonable guard choices given in the brief (PDF §11), used to score the terminal test.
REASONABLE = {"A": {"ATTACK"}, "B": {"TAKE_COVER", "RETREAT"},
              "C": {"RELOAD", "TAKE_COVER", "CALL_FOR_HELP"}, "D": {"RETREAT", "CALL_FOR_HELP"}}

# Small context, all layers on the GPU (Ollama's own estimate leaves ~1 GB unused and puts 38% on the CPU,
# ~4x slower), capped output. Warm-up uses the same options, otherwise Ollama reloads the model.
LOAD_OPTS = {"num_ctx": 2048, "num_gpu": 99}
TOOL_OPTS = {**LOAD_OPTS, "temperature": 0, "num_predict": 100}
FINAL_OPTS = {**LOAD_OPTS, "temperature": 0, "num_predict": 200}
llm = ollama.AsyncClient()


@asynccontextmanager
async def mcp_session():
    params = StdioServerParameters(command=sys.executable, args=[str(HERE / "npc_server.py")])
    async with stdio_client(params) as (r, w), ClientSession(r, w) as s:
        await s.initialize()
        yield s


def health_word(hp: float) -> str:
    return "critical" if hp < 25 else "low" if hp < 50 else "medium" if hp < 75 else "high"


def ammo_word(ammo: int, mx: int) -> str:
    return "empty" if ammo == 0 else "full" if ammo == mx else "low" if ammo <= mx * 0.3 else "partial"


def contradictions(reason: str, npc: dict, env: dict) -> list[str]:
    """Claims in the model's reason that the facts disprove (hallucination check).
    ponytail: keyword check for the common claims only; a second LLM judge would catch more at ~1 s extra."""
    r = " " + reason.lower()
    said = lambda *words: any(re.search(rf"(?<!not )(?<!no longer ){w}", r) for w in words)
    hp, ammo, mx, n = npc["health"], npc["ammo"], npc["maxAmmo"], npc["enemyCount"]
    found = []
    if ammo > 0 and said(r"no ammo", r"out of ammo", r"empty weapon", r"ammo is empty", r"empty ammo"):
        found.append(f"ammo is {ammo}/{mx}, not empty")
    if ammo > mx * 0.3 and said(r"low ammo", r"ammo is low", r"limited ammo", r"only \d+ (ammo|rounds|bullets)"):
        found.append(f"ammo is {ammo}/{mx}, not low")
    if n:   # which side of each weapon range the NPC is on
        d, theirs, mine = npc["enemyDistance"], r"(their|theirs|the enemy'?s?|enemy|enemies'?)", r"(my|mine|your|yours)"
        if d <= env["enemyWeaponRange"] and said(rf"outside {theirs}( weapon)? range", rf"out of {theirs}( weapon)? range"):
            found.append(f"the enemy is {d:g} m away and can shoot {env['enemyWeaponRange']:g} m, so you ARE inside their range")
        if d > env["enemyWeaponRange"] and said(rf"(inside|within) {theirs}( weapon)? range", r"inside theirs", r"within theirs"):
            found.append(f"the enemy is {d:g} m away and can shoot only {env['enemyWeaponRange']:g} m, so you are OUTSIDE their range")
        if d <= env["weaponRange"] and said(rf"outside {mine}( weapon)? range", r"outside mine", rf"out of {mine}( weapon)? range", r"they are outside"):
            found.append(f"the enemy is {d:g} m away and you can shoot {env['weaponRange']:g} m, so the enemy IS inside your range")
    if hp >= 50 and said(r"low health", r"critical health", r"health is low", r"badly hurt"):
        found.append(f"health is {hp:g}/100, not low")
    # "outnumbered by allies" is a wording slip for "we outnumber them", not a wrong belief
    if n <= 1 + npc["nearbyAllies"] and said(r"outnumbered(?! by (my |our |the |\d+ )?all)"):
        found.append(f"{n} enemies vs you + {npc['nearbyAllies']} nearby allies, so you are not outnumbered")
    in_cover = env.get("inCover", False)
    if npc["coverAvailable"] and said(r"no cover (is )?(available|nearby|in reach|within reach)", r"there is no cover", r"without any cover"):
        found.append(f"cover is available {env['nearestCoverDistance']:g} m away")
    if in_cover and said(r"not in cover", r"under no cover", r"not behind cover", r"exposed", r"in the open", r"no cover"):
        found.append("you are already IN cover that blocks the nearest enemy")
    if not in_cover and said(r"already in cover", r"already behind cover", r"(?<!not )in cover now"):
        found.append("you are NOT in cover yet")
    if not env["isUnderFire"] and said(r"under fire", r"taking fire", r"being shot"):
        found.append("you are not under fire")
    return found


def fact_sheet(obj: dict, npc: dict, env: dict) -> str:
    """Restates the tool results as plain facts (no advice), so the model does not misread raw JSON."""
    d, n, er, wr = npc["enemyDistance"], npc["enemyCount"], env["enemyWeaponRange"], env["weaponRange"]
    hp, ammo, mx, team = npc["health"], npc["ammo"], npc["maxAmmo"], 1 + npc["nearbyAllies"]
    lines = [f"Role: {obj['role']}. Goal: {obj['goal']}",
             f"Health: {hp:g}/100 ({health_word(hp)})",
             f"Ammo: {ammo}/{mx} ({ammo_word(ammo, mx)})",
             f"Visible enemies: {n}" + (f", nearest {d:g} m away" if n else "")]
    if n:
        lines += [f"Enemy weapon range: {er:g} m, so you are {'INSIDE' if d <= er else 'OUTSIDE'} their range",
                  f"Your weapon range: {wr:g} m, so the nearest enemy is {'inside' if d <= wr else 'outside'} your range",
                  f"Force balance: you + {npc['nearbyAllies']} nearby allies = {team} vs {n} enemies "
                  f"({'you are outnumbered' if n > team else 'even' if n == team else 'you have more'})"]
    lines += [f"Allies within 10 m: {npc['nearbyAllies']}; allies that can still be called: {npc['alliesCallable']}",
              "Cover: " + ("you are IN cover, it blocks the nearest enemy's shots" if env.get("inCover") else
                           f"available {env['nearestCoverDistance']:g} m away, but you are NOT in cover yet" if npc["coverAvailable"] else
                           "none within reach"),
              f"Under fire right now: {'yes' if env['isUnderFire'] else 'no'}",
              f"Current action: {npc['currentAction']}"]
    return "\n".join(lines)


def tool_text(res) -> str:
    # FastMCP splits a list result into one text block per item; the whole list is in structuredContent.
    data = res.structuredContent
    if data and "result" in data:
        return json.dumps(data["result"])
    return res.content[0].text if res.content else ""


async def decide(s: ClientSession) -> dict:
    listed = (await s.list_tools()).tools
    tools = [{"type": "function", "function": {
        "name": t.name, "description": t.description, "parameters": t.inputSchema}} for t in listed]
    known = {t.name for t in listed}
    msgs = [{"role": "system", "content": SYSTEM},
            {"role": "user", "content": "What should this NPC do now?"}]
    results: dict[str, str] = {}   # one call per tool and decision; repeats get the same answer
    called: list[str] = []

    async def call(name: str, args) -> str:
        if name not in known:
            return f"error: unknown tool '{name}'. Available tools: {', '.join(sorted(known))}"
        if name not in results:
            res = await s.call_tool(name, args or {})
            results[name] = ("error: " if res.isError else "") + tool_text(res)
        return results[name]

    # Phase 1: bounded tool-calling loop (the model fetches what it needs through MCP)
    for _ in range(4):
        resp = await llm.chat(model=MODEL, messages=msgs, tools=tools, options=TOOL_OPTS, think=False, keep_alive="1h")
        msgs.append(resp.message)
        if not resp.message.tool_calls:
            break
        for c in resp.message.tool_calls:
            called.append(c.function.name)
            msgs.append({"role": "tool", "content": await call(c.function.name, c.function.arguments),
                         "tool_name": c.function.name})
        if all(t in results for t in REQUIRED_TOOLS):
            break   # everything needed is known; skip a round trip that would only produce text

    # Grounding guarantee: never decide without the NPC's objective, state and legal actions.
    for t in REQUIRED_TOOLS:
        if t not in results:
            called.append(f"{t} (by agent)")
            msgs.append({"role": "user", "content": f"Result of {t}: {await call(t, {})}"})

    legal = json.loads(results["get_legal_actions"])
    if not legal:
        raise ValueError("no legal actions")
    obj, npc, env = (json.loads(results[t]) for t in ("get_npc_objective", "get_npc_state", "get_environment_context"))
    facts = fact_sheet(obj, npc, env)

    # Phase 2: final decision constrained by JSON Schema; action enum = current legal actions
    schema = {"type": "object", "required": ["action", "confidence", "reason"],
              "properties": {
                  "action": {"type": "string", "enum": legal},
                  "confidence": {"type": "number", "minimum": 0, "maximum": 1},
                  "reason": {"type": "string"}}}
    msgs.append({"role": "user", "content": f"Facts from the tools:\n{facts}\n\nLegal actions right now: {', '.join(legal)}.\n"
                 "Return your decision as JSON: action (one of the legal actions), confidence (0-1), and reason "
                 "(one short sentence that states only facts listed above)."})
    # Check the model's output instead of trusting it (Unity checks legality again before executing).
    # A reason that contradicts the facts gets one correction round; a second failure is rejected.
    for attempt in range(2):
        final = await llm.chat(model=MODEL, messages=msgs, format=schema, options=FINAL_OPTS, think=False, keep_alive="1h")
        d = json.loads(final.message.content)
        if d.get("action") not in legal or not isinstance(d.get("confidence"), (int, float)):
            raise ValueError(f"invalid decision from model: {final.message.content}")
        reason = str(d.get("reason", "")).strip()
        wrong = contradictions(reason, npc, env)
        if not wrong:
            return {"action": d["action"], "confidence": min(max(float(d["confidence"]), 0.0), 1.0),
                    "reason": reason, "toolsCalled": called, "corrected": attempt > 0}
        msgs += [final.message, {"role": "user", "content": f"Your reason contradicts the facts: {'; '.join(wrong)}. "
                 "Re-evaluate the situation using only the facts listed above and answer again as JSON."}]
    raise ValueError(f"reason still contradicts the facts: {'; '.join(wrong)}")


# ---------- HTTP server for Unity ----------
session: ClientSession | None = None
lock = asyncio.Lock()   # state.json is shared, so decisions are made one at a time
model_ready = False     # model loaded in Ollama and answering; reported by GET /health
warming: asyncio.Task | None = None


async def warm_up():
    """Loads the model into memory so the next decision is not slow (also after Ollama was restarted)."""
    global model_ready
    try:
        await llm.generate(model=MODEL, prompt="", options=LOAD_OPTS, keep_alive="1h")
        model_ready = True
    except Exception as e:
        model_ready = False
        print(f"WARNING: Ollama not reachable ({type(e).__name__}: {e}). Unity uses its fallback meanwhile.")


def ensure_warm():
    global warming
    if not model_ready and (warming is None or warming.done()):
        warming = asyncio.create_task(warm_up())


@asynccontextmanager
async def lifespan(_app):
    global session
    async with mcp_session() as s:
        session = s
        await warm_up()
        yield


app = FastAPI(lifespan=lifespan)


@app.get("/health")
async def health() -> dict:
    """Ready = Ollama answers and the model is loaded in memory. If not, loading starts in the background."""
    global model_ready
    try:
        loaded = await asyncio.wait_for(llm.ps(), 1.5)
        model_ready = any(MODEL in (m.model, getattr(m, "name", None)) for m in loaded.models)
    except Exception:
        model_ready = False
    ensure_warm()
    return {"ready": model_ready, "model": MODEL}


@app.post("/decide")
async def decide_endpoint(req: dict):
    global model_ready
    if not isinstance(req.get("stateVersion"), int) or not all(k in req for k in ("npc", "legalActions", "environment", "objective")):
        return JSONResponse({"error": "malformed request"}, status_code=400)
    async with lock:
        STATE_FILE.write_text(json.dumps(req), encoding="utf-8")
        try:
            d = await asyncio.wait_for(decide(session), DECISION_TIMEOUT)
        except Exception as e:   # any failure -> HTTP error -> Unity runs its deterministic fallback
            print(f"decision failed: {type(e).__name__}: {e}")
            if not isinstance(e, ValueError):   # Ollama down or too slow, not a bad answer: reload the model
                model_ready = False
                ensure_warm()
            return JSONResponse({"error": f"{type(e).__name__}: {e}"}, status_code=503)
    result = {"stateVersion": req["stateVersion"], **d}
    print(f"[decision] v{req['stateVersion']} {req['objective']['role']}  legal={req['legalActions']}\n"
          f"           MCP tools called: {', '.join(d['toolsCalled'])}\n           JSON: {json.dumps(result)}")
    return result


# ---------- terminal test without Unity ----------
async def run_scenarios(args: list[str]):
    scenarios = json.loads((HERE / "scenarios.json").read_text(encoding="utf-8"))
    names = [a for a in args if a in scenarios] or list(scenarios)
    roles = [a for a in args if a in ROLES] or ["Guard"]
    # self-check of the hallucination filter (scenario A: full health, full ammo, 1 enemy vs guard + 1 ally, under fire)
    a = scenarios["A"]
    assert contradictions("Low ammo and outnumbered.", a["npc"], a["environment"]) == [
        "ammo is 10/10, not low", "1 enemies vs you + 1 nearby allies, so you are not outnumbered"]
    assert contradictions("Not outnumbered, healthy and under fire.", a["npc"], a["environment"]) == []
    assert len(contradictions("I am inside the enemy's range while they are outside mine.", a["npc"], a["environment"])) == 1
    in_cover = {**a["environment"], "inCover": True}
    assert contradictions("You are currently under no cover despite having it available.", a["npc"], in_cover) == [
        "you are already IN cover that blocks the nearest enemy"]
    assert contradictions("You are not in cover yet, so move to it.", a["npc"], a["environment"]) == []
    assert contradictions("Full ammo and outnumbered by allies.", a["npc"], a["environment"]) == []
    e = scenarios["E"]   # enemy at 12 m, enemy range 10 m
    assert len(contradictions("Outside enemy range while inside theirs.", e["npc"], e["environment"])) == 1
    async with mcp_session() as s:
        for name in names:
            for role in roles:
                state = {**scenarios[name], "objective": {"role": role, "goal": ROLES[role]}}
                STATE_FILE.write_text(json.dumps(state), encoding="utf-8")
                t = time.perf_counter()
                try:
                    d = await decide(s)
                except ValueError as e:
                    print(f"{name} [{role}]: REJECTED -> Unity fallback ({e})\n")
                    continue
                ms = (time.perf_counter() - t) * 1000
                check = "" if role != "Guard" or name not in REASONABLE else \
                    ("  [matches brief]" if d["action"] in REASONABLE[name] else "  [NOT in brief's examples]")
                fixed = "  (reason corrected once)" if d["corrected"] else ""
                print(f"{name} [{role}]: {d['action']}  conf={d['confidence']:.2f}  {ms:.0f} ms{check}{fixed}\n"
                      f"   tools={d['toolsCalled']}\n   legal={state['legalActions']}\n   reason: {d['reason']}\n")


if __name__ == "__main__":
    if sys.argv[1:2] == ["test"]:
        asyncio.run(run_scenarios(sys.argv[2:]))
    else:
        uvicorn.run(app, host="127.0.0.1", port=8000)
