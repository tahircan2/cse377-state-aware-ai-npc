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

    public void Load(int i)
    {
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
        GUI.Label(new Rect(10, Screen.height - 70, 900, 25),
            "Yellow = Guard    Blue = Player (enemy, move with WASD)    Red = Enemy    Green = Ally    Grey = Cover");
        for (int i = 0; i < Scenarios.Length; i++)
            if (GUI.Button(new Rect(10 + i * 170, Screen.height - 45, 160, 35), Scenarios[i].name)) Load(i);
        // Same scenario, different personality (PDF §16)
        if (GUI.Button(new Rect(10 + Scenarios.Length * 170, Screen.height - 45, 160, 35), $"Role: {guard.Objective.role}"))
            guard.SetRole((guard.roleIndex + 1) % GuardNpc.Roles.Length);
    }
}
