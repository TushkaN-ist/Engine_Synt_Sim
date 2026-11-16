using UnityEditor;
using UnityEngine;

namespace RpmSynth.Editor
{
    [CustomEditor(typeof(EngineSoundPlayer)), CanEditMultipleObjects]
    internal sealed class EngineSoundPlayerEditor : UnityEditor.Editor
    {
        private bool advanced;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(true))
                Draw("m_Script");
            var profile = serializedObject.FindProperty("profile");
            EditorGUILayout.PropertyField(profile);
            Draw("gain");

            advanced = EditorGUILayout.Foldout(advanced, "Advanced Playback", true);
            if (advanced)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    Draw("rpm");
                    Draw("load");
                    var asset = profile.objectReferenceValue as EngineSoundProfile;
                    if (profile.hasMultipleDifferentValues || asset == null || !asset.HasSavedNoiseAndVariation)
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("texture"),
                            new GUIContent("Legacy Noise and Variation", "Used by profiles saved before noise was stored in the profile."));
                    Draw("mechanicalDetail");
                    Draw("resonantAttacks");
                }
            }
            serializedObject.ApplyModifiedProperties();
        }

        private void Draw(string name) => EditorGUILayout.PropertyField(serializedObject.FindProperty(name));
    }
}
