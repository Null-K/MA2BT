using UnityEngine;
using VRC.SDKBase;
#if UNITY_EDITOR
using UnityEditor;
#endif

[AddComponentMenu("MA2BT/MA2BT")]
[DisallowMultipleComponent]
public class MAToBlendTree : MonoBehaviour, IEditorOnly
{
    [Tooltip("Compact mode: only generate thresholds where animations exist, reducing empty clips.")]
    public bool compactMode = true;

    [Tooltip("Attempt to convert multi-state layers (layers with more than one conditional state).")]
    public bool convertMultiState = false;

    [Tooltip("Scan all FX layers, not just MA-generated ones.")]
    public bool scanAllLayers = false;
}

#if UNITY_EDITOR
[CustomEditor(typeof(MAToBlendTree))]
public class MAToBlendTreeEditor : Editor
{
    static readonly Color HeaderColor = new Color(0.55f, 0.2f, 0.85f);
    const string VERSION = "2.0.2";

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // 头部标题
        EditorGUILayout.Space(4);
        var headerRect = EditorGUILayout.GetControlRect(false, 22);
        EditorGUI.DrawRect(headerRect, HeaderColor);
        var headerStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white },
            fontSize = 13
        };
        EditorGUI.LabelField(headerRect, $"MA2BT  v{VERSION}", headerStyle);
        EditorGUILayout.Space(6);

        // 设置
        DrawToggle("compactMode", "Compact Mode", "Only generate thresholds at values that have animations.");
        DrawToggle("convertMultiState", "Multi-State Layers", "Also convert layers with multiple conditional states.");
        DrawToggle("scanAllLayers", "Scan All Layers", "Include non-MA layers in the optimization scan.");

        // 页脚
        EditorGUILayout.Space(4);
        var footerStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(0.5f, 0.5f, 0.5f) }
        };
        EditorGUILayout.LabelField("by PuddingKC", footerStyle);

        serializedObject.ApplyModifiedProperties();
    }

    void DrawToggle(string propName, string label, string tooltip)
    {
        var prop = serializedObject.FindProperty(propName);
        if (prop == null) return;
        prop.boolValue = EditorGUILayout.ToggleLeft(new GUIContent(label, tooltip), prop.boolValue);
    }
}
#endif
