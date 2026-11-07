using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Builds Assets/Scenes/NpcScene.unity from Unity primitives only (menu: CSE377 > Build Scene).
public static class SceneBuilder
{
    const string ScenePath = "Assets/Scenes/NpcScene.unity";

    [MenuItem("CSE377/Build Scene")]
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects);   // camera + light
        Camera.main.transform.SetPositionAndRotation(new Vector3(0, 28, -22), Quaternion.Euler(58, 0, 0));   // whole 40x40 arena in view

        Make(PrimitiveType.Plane, "Ground", Vector3.zero, Mat("Ground", new Color(0.55f, 0.6f, 0.5f)))
            .transform.localScale = new Vector3(4, 1, 4);

        var guard = Make(PrimitiveType.Capsule, "Guard_01", new Vector3(0, 1, 0), Mat("Guard", Color.yellow)).AddComponent<GuardNpc>();
        guard.gameObject.AddComponent<NpcBrain>();

        var red = Mat("Enemy", new Color(0.85f, 0.15f, 0.15f));
        guard.enemies = new Enemy[4];
        for (int i = 0; i < 4; i++)
        {
            var go = i == 0 ? Make(PrimitiveType.Capsule, "Player", Vector3.zero, Mat("Player", new Color(0.2f, 0.4f, 1f)))
                            : Make(PrimitiveType.Capsule, $"Enemy_{i}", Vector3.zero, red);
            guard.enemies[i] = go.AddComponent<Enemy>();
            guard.enemies[i].guard = guard;
        }

        var green = Mat("Ally", new Color(0.2f, 0.75f, 0.3f));
        guard.allies = new Transform[3];
        for (int i = 0; i < 3; i++) guard.allies[i] = Make(PrimitiveType.Capsule, $"Ally_{i + 1}", Vector3.zero, green).transform;

        var grey = Mat("Cover", Color.grey);
        Vector3[] coverPos = { new(-2.5f, 0.75f, 2.5f), new(2.5f, 0.75f, 2.5f), new(-4, 0.75f, -3) };
        guard.covers = new Transform[coverPos.Length];
        for (int i = 0; i < coverPos.Length; i++)
        {
            guard.covers[i] = Make(PrimitiveType.Cube, $"Cover_{i + 1}", coverPos[i], grey).transform;
            guard.covers[i].localScale = new Vector3(1.2f, 1.5f, 1.2f);
        }

        new GameObject("ScenarioLoader").AddComponent<ScenarioLoader>().guard = guard;

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
    }

    static GameObject Make(PrimitiveType type, string name, Vector3 pos, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.position = pos;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }

    static Material Mat(string name, Color color)
    {
        if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
        var mat = new Material(Shader.Find("Standard")) { color = color };
        AssetDatabase.CreateAsset(mat, $"Assets/Materials/{name}.mat");
        return mat;
    }
}
