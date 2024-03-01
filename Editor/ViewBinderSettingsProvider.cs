using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AutomaticViewBinding
{
    public static class ViewBinderSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider CreateViewBinderSettingsProvider()
        {
            var provider = new SettingsProvider("Project/Automatic View Binding for Unity", SettingsScope.Project)
            {
                label = "Automatic View Binding for Unity Settings",
                guiHandler = (searchContext) =>
                {
                    var settings = ViewBinderSettings.GetSerializedSettings();
                    EditorGUILayout.PropertyField(settings.FindProperty("generatedNamespace"), new GUIContent("Generated namespace"));
                    EditorGUILayout.PropertyField(settings.FindProperty("prefabSuffix"), new GUIContent("Prefab name suffix"));
                    EditorGUILayout.PropertyField(settings.FindProperty("showWarningLogs"), new GUIContent("Show warnings"));
                    settings.ApplyModifiedPropertiesWithoutUndo();
                },
                keywords = new HashSet<string>(new[] { "ViewBinding", "Bind" })
            };

            return provider;
        }
    }
}