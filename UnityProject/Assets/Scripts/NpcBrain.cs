using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using static LegalActionGenerator;

// AI decision pipeline: asks the local agent at meaningful moments (PDF §12), never blocks
// the game (§13), validates every answer against the CURRENT state (§9, §14) and logs it (§15).
// Unity keeps final authority: the AI only proposes an action name.
[RequireComponent(typeof(GuardNpc))]
public class NpcBrain : MonoBehaviour
{
    public string agentUrl = "http://127.0.0.1:8000/decide";
    // PDF §13 suggests 2-5 s as an example. A local 9B model on a 4 GB laptop GPU answers in ~3-4.5 s, so 8 s
    // keeps normal decisions from falling back when the GPU is also busy (projector, screen recording).
    // A constant, not an Inspector field: a value saved in the scene would silently override it.
    const int TimeoutSeconds = 8;
    public float decisionInterval = 3;    // timer-based decision every 3 s ...
    public float minEventGap = 1;         // ... or sooner on an event, but never every frame
    public float healthCheckInterval = 2;

    GuardNpc guard;
    bool pending, aiReady;
    string aiStatus = "AI: connecting...";
    float lastRequest = -99;
    int lastVersion = -1;
    string lastDecision = "", lastTools = "-";
    string logPath;
    GUIStyle hud;

    void Awake()
    {
        guard = GetComponent<GuardNpc>();
        Application.targetFrameRate = 60;   // an uncapped frame rate steals the GPU from the local LLM
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;   // "0.95", not "0,95"
        logPath = Path.Combine(Application.dataPath, "..", "decisions.log");
    }

    void Start() => StartCoroutine(HealthLoop());

    // Asks the agent whether the model is loaded. While it is not, the guard plays on the fallback
    // instead of waiting for requests that would only time out (PDF §13).
    IEnumerator HealthLoop()
    {
        while (true)
        {
            using (var www = UnityWebRequest.Get(agentUrl.Replace("/decide", "/health")))
            {
                www.timeout = 2;
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success) { aiReady = false; aiStatus = "AI: offline (agent not running) - using fallback"; }
                else
                {
                    aiReady = www.downloadHandler.text.Contains("\"ready\":true");
                    aiStatus = aiReady ? "AI: ready" : "AI: loading model... - using fallback";
                }
            }
            yield return new WaitForSeconds(healthCheckInterval);
        }
    }

    void Update()
    {
        if (pending || guard.IsDead || guard.GetState().enemyCount == 0) return;   // no threat, nothing to decide
        float since = Time.time - lastRequest;
        bool gameEvent = guard.StateVersion != lastVersion || guard.ActionFinished;
        if (since < decisionInterval && !(gameEvent && since >= minEventGap)) return;
        if (aiReady) StartCoroutine(RequestDecision());
        else DecideOffline();
    }

    void DecideOffline()
    {
        lastRequest = Time.time;
        lastVersion = guard.StateVersion;
        guard.ActionFinished = false;
        var req = BuildRequest();
        string executed = Fallback();
        guard.Execute(executed);
        Log(req, null, "", 0, "SKIPPED (AI not ready)", executed);
    }

    DecisionRequest BuildRequest()
    {
        var state = guard.GetState();
        return new DecisionRequest
        {
            stateVersion = guard.StateVersion,
            npc = state,
            legalActions = Generate(state).ToArray(),
            environment = guard.GetEnvironment(),
            objective = guard.Objective,
        };
    }

    IEnumerator RequestDecision()
    {
        pending = true;
        lastRequest = Time.time;
        lastVersion = guard.StateVersion;
        guard.ActionFinished = false;
        var req = BuildRequest();

        float t0 = Time.realtimeSinceStartup;
        using var www = UnityWebRequest.Post(agentUrl, JsonUtility.ToJson(req), "application/json");
        www.timeout = TimeoutSeconds;
        yield return www.SendWebRequest();   // the guard keeps doing its current action meanwhile
        int latency = (int)((Time.realtimeSinceStartup - t0) * 1000);
        string raw = www.downloadHandler?.text ?? "";

        Decision d = null;
        string validation;
        if (www.result != UnityWebRequest.Result.Success)
            validation = $"FAIL ({www.error})";
        else
        {
            try { d = JsonUtility.FromJson<Decision>(raw); } catch (ArgumentException) { }
            validation = Validate(d);
        }

        if (!guard.IsDead)
        {
            string executed;
            bool stale = req.stateVersion != guard.StateVersion;   // the world changed while waiting (also for failed requests)
            if (validation == "PASS") executed = d.action;
            else if (stale) { executed = null; if (!validation.Contains("stale")) validation += " (state changed meanwhile, ignored)"; }   // keep current behaviour, a fresh decision follows
            else executed = Fallback();
            if (executed != null) guard.Execute(executed);
            Log(req, d, raw, latency, validation, executed ?? $"{guard.CurrentAction} (kept)");
        }
        pending = false;
    }

    // The game may have changed while the model was thinking, so check again before executing.
    string Validate(Decision d)
    {
        if (d == null || string.IsNullOrEmpty(d.action) || d.confidence < 0 || d.confidence > 1) return "REJECTED (malformed response)";
        if (d.stateVersion != guard.StateVersion) return $"REJECTED (stale: v{d.stateVersion}, now v{guard.StateVersion})";
        if (!Generate(guard.GetState()).Contains(d.action)) return $"REJECTED ({d.action} is not legal now)";
        return "PASS";
    }

    // Deterministic fallback when the AI fails, times out or returns an unusable action (PDF §13):
    // get out of the line of fire - into cover if there is any, otherwise away from the enemy (both always legal then).
    string Fallback() => guard.GetState().coverAvailable ? TakeCover : Retreat;

    void Log(DecisionRequest req, Decision d, string raw, int latency, string validation, string executed)
    {
        string tools = d?.toolsCalled == null ? "" : string.Join(", ", d.toolsCalled);
        string entry =
            $"[{DateTime.Now:HH:mm:ss}] NPC: {req.npc.npcId}  Role: {req.objective.role}  stateVersion: {req.stateVersion}\n" +
            $"  State: {JsonUtility.ToJson(req.npc)}  Environment: {JsonUtility.ToJson(req.environment)}\n" +
            $"  Legal actions: {string.Join(", ", req.legalActions)}\n" +
            $"  MCP tools called: {tools}\n" +
            $"  Raw AI response: {(raw == "" ? "-" : raw)}\n" +
            $"  Decision: {d?.action ?? "-"}  Confidence: {d?.confidence:0.00}  Reason: {d?.reason}{(d != null && d.corrected ? "  (reason corrected once by the agent's fact check)" : "")}\n" +
            $"  Latency: {latency} ms  Validation: {validation}  Executed: {executed}\n";
        Debug.Log(entry);
        File.AppendAllText(logPath, entry + "\n");
        if (d != null) lastTools = tools;
        lastDecision = $"Last decision: {(d == null ? "-" : $"{d.action} ({d.confidence:0.00})")}  {validation}  {latency} ms  ->  executed {executed}\n{d?.reason}";
    }

    void OnGUI()
    {
        hud ??= new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.UpperLeft, wordWrap = true, normal = { textColor = Color.white }, richText = true };
        var s = guard.GetState();
        string status = guard.IsDead ? "DEAD (load a scenario)" : s.enemyCount == 0 ? "no enemy in sight - waiting" : pending ? "AI thinking..." : "";
        GUI.Box(new Rect(10, 10, 820, 150),
            $"{s.npcId} [{guard.Objective.role}]   HP {s.health}   Ammo {s.ammo}/{s.maxAmmo}   Enemies {(s.enemyCount > 0 ? $"{s.enemyCount} @ {s.enemyDistance} m" : "0")}   Allies {s.nearbyAllies}   Cover {(s.coverAvailable ? "yes" : "no")}\n" +
            $"<b>Legal now:</b> {string.Join(", ", Generate(s))}\n" +
            $"<b>MCP tools (last decision):</b> {lastTools}\n" +
            $"Current action: {guard.CurrentAction}   {status}   <i>{aiStatus}</i>\n{lastDecision}", hud);
    }
}
