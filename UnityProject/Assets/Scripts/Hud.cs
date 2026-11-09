using UnityEngine;

// Shared HUD helpers: one UI scale for every screen size and consistent, readable styles.
public static class Hud
{
    const float RefHeight = 620;   // at this screen height the UI is drawn 1:1; larger screens scale it up
    public static float Scale => Mathf.Max(0.8f, Screen.height / RefHeight);
    public static Vector2 Size => new(Screen.width / Scale, Screen.height / Scale);

    public static GUIStyle Panel, Title, Text, Small, Button, Tag;

    // Call at the start of OnGUI: scales the UI and creates the styles once.
    public static void Begin()
    {
        GUI.matrix = Matrix4x4.Scale(new Vector3(Scale, Scale, 1));
        if (Panel != null && Panel.normal.background) return;   // the texture is lost when Play mode stops
        var bg = new Texture2D(1, 1);
        bg.SetPixel(0, 0, new Color(0.05f, 0.07f, 0.1f, 0.82f));
        bg.Apply();
        Panel = new GUIStyle { normal = { background = bg }, padding = new RectOffset(12, 12, 10, 10) };
        Text = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = true, wordWrap = true, normal = { textColor = Color.white } };
        Small = new GUIStyle(Text) { fontSize = 13, normal = { textColor = new Color(0.8f, 0.85f, 0.9f) } };
        Title = new GUIStyle(Text) { fontSize = 17, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.85f, 0.3f) } };
        Button = new GUIStyle(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold };
        Tag = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerCenter, richText = true };
    }

    public static void Space(float px = 4) => GUILayout.Space(px);
    public static string Paint(string text, string hex) => $"<color=#{hex}>{text}</color>";
}
