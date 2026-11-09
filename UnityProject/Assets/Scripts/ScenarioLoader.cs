using UnityEngine;

// Test harness for PDF §11: each button arranges the scene for Scenario A-E.
// The guard then measures its state from the world, exactly as in normal play.
public class ScenarioLoader : MonoBehaviour
{
    public GuardNpc guard;

    static readonly (string name, float health, int ammo, int enemies, float distance, int allies, bool cover)[] Scenarios =
    {
        ("A  Strong Position", 90, 10, 1, 5, 1, true),
        ("B  Critical Health", 15, 7, 2, 4, 0, true),
        ("C  No Ammunition", 70, 0, 1, 7, 2, true),
        ("D  Outnumbered", 40, 6, 4, 8, 0, false),
        ("E  Supported Defense", 55, 4, 2, 12, 3, true),
    };

    void Awake()
    {
        var player = guard.enemies[0].gameObject;   // enemies[0] is the Player capsule
        if (!player.GetComponent<PlayerController>()) player.AddComponent<PlayerController>();
    }

    void Start() => Load(0);

    public static int Count => Scenarios.Length;

    int current;

    public void Load(int i)
    {
        current = i;
        var s = Scenarios[i];
        for (int k = 0; k < guard.enemies.Length; k++)
        {
            var e = guard.enemies[k];
            e.gameObject.SetActive(k < s.enemies);
            e.health = 100;
            float angle = (k - (s.enemies - 1) / 2f) * 20;   // on an arc, so every enemy is s.distance away
            e.transform.position = Quaternion.Euler(0, angle, 0) * Vector3.forward * s.distance + Vector3.up;
        }
        for (int k = 0; k < guard.allies.Length; k++)    // the others wait far away and only come if called
            guard.allies[k].position = k < s.allies ? new Vector3(-2 - 1.5f * k, 1, -2) : new Vector3(-8 + 8 * k, 1, -17);
        foreach (var c in guard.covers) c.gameObject.SetActive(s.cover);
        guard.ResetTo(s.health, s.ammo);
    }

    void OnGUI()
    {
        GUI.depth = 1;   // behind the HUD panels
        Hud.Begin();
        DrawTags();

        // Bottom bar: legend, scenario buttons and the role switch (PDF §11, §16)
        var size = Hud.Size;
        float gap = 8, h = 38, n = Scenarios.Length + 1;
        float bw = (size.x - 20 - gap * (n - 1)) / n;
        GUILayout.BeginArea(new Rect(0, size.y - h - 44, size.x, h + 44));
        GUILayout.BeginVertical(Hud.Panel);
        GUILayout.Label($"{Hud.Paint("■ Guard", "ffe14d")}   {Hud.Paint("■ Player (enemy) - move with WASD", "5b8cff")}   " +
                        $"{Hud.Paint("■ Enemy", "ff5555")}   {Hud.Paint("■ Ally", "4fd16b")}   {Hud.Paint("■ Cover", "b0b0b0")}", Hud.Small);
        GUILayout.EndVertical();
        GUILayout.EndArea();
        for (int i = 0; i < Scenarios.Length; i++)
        {
            GUI.backgroundColor = i == current ? new Color(1f, 0.85f, 0.3f) : Color.white;
            if (GUI.Button(new Rect(10 + i * (bw + gap), size.y - h - 6, bw, h), Scenarios[i].name, Hud.Button)) Load(i);
        }
        GUI.backgroundColor = new Color(0.6f, 0.85f, 1f);
        if (GUI.Button(new Rect(10 + Scenarios.Length * (bw + gap), size.y - h - 6, bw, h), $"Role: {guard.Objective.role}", Hud.Button))
            guard.SetRole((guard.roleIndex + 1) % GuardNpc.Roles.Length);
        GUI.backgroundColor = Color.white;
    }

    // A name tag above every unit, so the scene explains itself on a projector.
    void DrawTags()
    {
        Tag(guard.transform, 2.3f, $"GUARD\n{guard.CurrentAction}", "ffe14d");
        for (int i = 0; i < guard.enemies.Length; i++)
            Tag(guard.enemies[i].transform, 2.3f, i == 0 ? $"PLAYER  {guard.enemies[i].health:0} HP" : $"ENEMY {i}  {guard.enemies[i].health:0} HP", i == 0 ? "7fa6ff" : "ff6b6b");
        for (int i = 0; i < guard.allies.Length; i++) Tag(guard.allies[i], 2.3f, $"ALLY {i + 1}", "4fd16b");
        foreach (var c in guard.covers) Tag(c, 1.0f, "COVER", "d0d0d0");
    }

    void Tag(Transform t, float height, string text, string hex)
    {
        if (!t.gameObject.activeInHierarchy || !Camera.main) return;
        var p = Camera.main.WorldToScreenPoint(t.position + Vector3.up * height);
        if (p.z < 0) return;
        var center = new Vector2(p.x, Screen.height - p.y) / Hud.Scale;
        var rect = new Rect(center.x - 80, center.y - 40, 160, 40);
        GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), $"<color=#000000cc>{text}</color>", Hud.Tag);   // shadow
        GUI.Label(rect, Hud.Paint(text, hex), Hud.Tag);
    }
}
