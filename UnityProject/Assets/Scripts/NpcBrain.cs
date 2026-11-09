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
    string lastTools = "-", lastAction = "-", lastValidation = "-", lastExecuted = "-", lastReason = "";
    float lastConfidence;
    int lastLatency;
    string logPath;

    void Awake()
    {
        guard = GetComponent<GuardNpc>();
        // The game and the local LLM share one laptop GPU: rendering fewer frames leaves it free for the model.
        QualitySettings.vSyncCount = 0;     // otherwise targetFrameRate is ignored
        Application.targetFrameRate = 30;
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
        (lastAction, lastConfidence, lastReason) = d == null ? ("-", 0f, "") : (d.action, d.confidence, d.reason);
        (lastValidation, lastLatency, lastExecuted) = (validation, latency, executed);
    }

    // ---------- HUD: guard state on the left, AI decision on the right ----------
    void OnGUI()
    {
        GUI.depth = 0;   // above the unit tags
        Hud.Begin();
        var size = Hud.Size;
        float w = Mathf.Min(440, size.x / 2 - 15);
        var s = guard.GetState();
        var env = guard.GetEnvironment();

        GUILayout.BeginArea(new Rect(10, 10, w, size.y - 110));
        GUILayout.BeginVertical(Hud.Panel);
        GUILayout.Label($"GUARD  ·  {s.npcId}  ·  {guard.Objective.role}", Hud.Title);
        Hud.Space();
        Row("Health", $"{s.health:0} / 100", s.health < 25 ? "ff5555" : s.health < 50 ? "ffb347" : "7CFC7C");
        Row("Ammo", $"{s.ammo} / {s.maxAmmo}", s.ammo == 0 ? "ff5555" : "ffffff");
        Row("Enemies", s.enemyCount > 0 ? $"{s.enemyCount}  (nearest {s.enemyDistance} m)" : "none in sight");
        Row("Allies", $"{s.nearbyAllies} nearby, {s.alliesCallable} can be called");
        Row("Cover", s.coverAvailable ? $"yes ({env.nearestCoverDistance} m)" : "no");
        Row("Under fire", env.isUnderFire ? "yes" : "no", env.isUnderFire ? "ff5555" : "ffffff");
        Hud.Space();
        Row("Current action", $"<b>{guard.CurrentAction}</b>", "ffd24d");
        Row("stateVersion", guard.StateVersion.ToString());
        if (guard.IsDead) GUILayout.Label(Hud.Paint("DEAD - load a scenario", "ff5555"), Hud.Text);
        else if (s.enemyCount == 0) GUILayout.Label(Hud.Paint("No enemy in sight - waiting", "aaaaaa"), Hud.Small);
        GUILayout.EndVertical();
        GUILayout.EndArea();

        GUILayout.BeginArea(new Rect(size.x - w - 10, 10, w, size.y - 110));
        GUILayout.BeginVertical(Hud.Panel);
        GUILayout.Label("AI DECISION", Hud.Title);
        string ai = pending ? Hud.Paint("thinking...", "ffd24d") : aiReady ? Hud.Paint(aiStatus, "7CFC7C") : Hud.Paint(aiStatus, "ff7777");
        GUILayout.Label(ai, Hud.Small);
        Hud.Space();
        Row("Legal now", string.Join(", ", Generate(s)), "8fd3ff");
        Row("MCP tools", lastTools == "" ? "-" : lastTools.Replace("get_", ""));
        Hud.Space();
        Row("Decision", lastAction == "-" ? "-" : $"<b>{lastAction}</b>  ({lastConfidence:0.00})", "ffd24d");
        Row("Validation", lastValidation, lastValidation.StartsWith("PASS") ? "7CFC7C" : "ff9966");
        Row("Executed", lastExecuted);
        Row("Latency", $"{lastLatency} ms");
        if (lastReason != "") GUILayout.Label($"<i>\"{lastReason}\"</i>", Hud.Small);
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    static void Row(string name, string value, string hex = "ffffff") =>
        GUILayout.Label($"{Hud.Paint(name, "9aa5b1")}   {Hud.Paint(value, hex)}", Hud.Text);
}
