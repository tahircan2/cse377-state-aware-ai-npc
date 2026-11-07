using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Development-only end-to-end check in real Play mode (not part of the game):
//   Unity.exe -batchmode -projectPath UnityProject -executeMethod PlayModeSmokeTest.Run -logFile smoke.log
// Plays every scenario with every role against the running agent, then once with the agent unreachable,
// and writes smoke_test.txt. Exit code 0 = every check passed.
[InitializeOnLoad]
public static class PlayModeSmokeTest
{
    const string Flag = "CSE377_SmokeTest";

    static PlayModeSmokeTest()   // runs again after the domain reload that entering Play mode causes
    {
        EditorApplication.playModeStateChanged += s =>
        {
            if (s == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Flag, false))
                new GameObject("SmokeTestDriver").AddComponent<Driver>();
        };
    }

    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/NpcScene.unity");
        SessionState.SetBool(Flag, true);
        EditorApplication.EnterPlaymode();
    }

    class Driver : MonoBehaviour
    {
        readonly List<string> entries = new(), problems = new();
        readonly List<string> report = new();

        void Awake()
        {
            Application.logMessageReceived += (msg, stack, type) =>
            {
                if (msg.Contains("Validation:")) entries.Add(msg);
                else if (type is LogType.Error or LogType.Exception or LogType.Assert && !stack.Contains("UnityEditor.Search"))   // ignore the Editor's own search indexer
                    problems.Add($"{type}: {msg}\n{stack}");
            };
        }

        IEnumerator Start()
        {
            var loader = FindAnyObjectByType<ScenarioLoader>();
            var guard = loader.guard;
            var brain = guard.GetComponent<NpcBrain>();
            yield return new WaitForSeconds(1);

            for (int s = 0; s < ScenarioLoader.Count; s++)
                for (int r = 0; r < GuardNpc.Roles.Length; r++)
                {
                    guard.SetRole(r);
                    loader.Load(s);
                    yield return Play($"Scenario {"ABCDE"[s]} / {GuardNpc.Roles[r].role}", guard, 9);
                }

            brain.agentUrl = "http://127.0.0.1:8999/decide";   // nothing listens here: the AI is "down"
            guard.SetRole(0);
            loader.Load(3);
            yield return Play("Agent unreachable (Scenario D)", guard, 6);

            int executed = entries.Count(e => e.Contains("Executed:"));
            int pass = entries.Count(e => e.Contains("Validation: PASS"));
            report.Insert(0, $"Decisions: {entries.Count}  PASS: {pass}  Errors/exceptions: {problems.Count}\n");
            report.AddRange(problems);
            File.WriteAllText(Path.Combine(Application.dataPath, "..", "smoke_test.txt"), string.Join("\n", report));
            SessionState.SetBool(Flag, false);
            EditorApplication.Exit(problems.Count == 0 && executed == entries.Count && pass > 0 ? 0 : 1);
        }

        // Lets the game run for a while and records every decision made in that time.
        IEnumerator Play(string title, GuardNpc guard, float seconds)
        {
            int from = entries.Count;
            var start = guard.GetState();
            yield return new WaitForSeconds(seconds);
            var end = guard.GetState();
            report.Add($"=== {title}\nstart: HP {start.health} ammo {start.ammo} enemies {start.enemyCount} allies {start.nearbyAllies}" +
                       $"  ->  end: HP {end.health} ammo {end.ammo} enemies {end.enemyCount} allies {end.nearbyAllies} action {guard.CurrentAction}");
            report.AddRange(entries.Skip(from));
            if (entries.Count == from) report.Add("(no decision in this window)");
        }
    }
}
