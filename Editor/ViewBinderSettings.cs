using UnityEditor;
using UnityEngine;

namespace AutomaticViewBinding
{
    public class ViewBinderSettings : ScriptableObject
    {
        private const string SettingsPath = "Assets/Scripts/UnityViewBinding/ViewBinderSettings.asset";

#pragma warning disable CS0414 // Field is assigned but its value is never used
        [SerializeField] internal string generatedNamespace;

        [SerializeField] internal string prefabSuffix;

        [SerializeField] internal bool showWarningLogs;
#pragma warning restore CS0414 // Field is assigned but its value is never used

        internal static ViewBinderSettings GetOrCreateSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<ViewBinderSettings>(SettingsPath);
            if (settings == null)
            {
                settings = CreateInstance<ViewBinderSettings>();
                settings.generatedNamespace = "ViewBinding";
                settings.prefabSuffix = ".View";
                settings.showWarningLogs = false;
                if (!AssetDatabase.IsValidFolder("Assets/Scripts/UnityViewBinding"))
                {
                    AssetDatabase.CreateFolder("Assets/Scripts", "UnityViewBinding");
                }
                AssetDatabase.CreateAsset(settings, SettingsPath);
                AssetDatabase.SaveAssets();
            }
            return settings;
        }

        internal static SerializedObject GetSerializedSettings() => new(GetOrCreateSettings());
    }
}