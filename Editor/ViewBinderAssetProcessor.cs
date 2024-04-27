using System.Globalization;

namespace AutomaticViewBinding
{
    using System.Text;
    using System.Text.RegularExpressions;
    using UnityEditor;
    using UnityEngine;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using UnityEditor.SceneManagement;

    [InitializeOnLoad]
    public class ViewBinderAssetProcessor
    {
        private const string ShouldGenerateViewBinding = "ShouldGenerateViewBinding";
        private const string AssetPath = "AssetPath";
        private const string IgnoreTagName = "ViewBinderIgnore";

        static ViewBinderAssetProcessor()
        {
            TagManager.CreateTag(IgnoreTagName);
            PrefabStage.prefabStageClosing += OnPrefabUpdated;
        }

        private static void LogWarning(string log)
        {
            var settings = ViewBinderSettings.GetOrCreateSettings();
            if (settings.showWarningLogs)
            {
                Debug.LogWarning(log);
            }
        }

        private static void OnPrefabUpdated(PrefabStage prefabStage)
        {
            var settings = ViewBinderSettings.GetOrCreateSettings();
            if (!EditorUtility.IsDirty(prefabStage.prefabContentsRoot))
            {
                LogWarning("Asset not dirty, skipping");
                return;
            }

            if (!prefabStage.assetPath.EndsWith($"{settings.prefabSuffix}.prefab", true, CultureInfo.InvariantCulture))
            {
                LogWarning("Asset name does not match format, skipping");
                return;
            }

            try
            {
                if (CreateMono(prefabStage))
                    return;

                EditorPrefs.SetString(AssetPath, prefabStage.assetPath);
                EditorPrefs.SetBool(ShouldGenerateViewBinding, true);
                CreateViewBinding();
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
            finally
            {
                prefabStage.ClearDirtiness();
            }
        }

        /// <summary>
        /// Tries to create the partial MonoBehaviour so we can generate the ViewBinding
        /// </summary>
        /// <param name="prefabStage"></param>
        /// <returns>False if partial MonoBehaviour already exists, True if we needed to create</returns>
        private static bool CreateMono(PrefabStage prefabStage)
        {
            var settings = ViewBinderSettings.GetOrCreateSettings();
            var assetName = prefabStage.prefabContentsRoot.name;
            var parsedName = $"{Normalize(assetName)}Binding";
            var stringBuilder = new StringBuilder();
            stringBuilder.Append($@"namespace {settings.generatedNamespace}
{{
using UnityEngine;
public partial class {parsedName} : MonoBehaviour {{}}
}}
");

            var scriptPath = $"Assets/Scripts/UnityViewBinding/{parsedName}.cs";
            var fileExists = File.Exists(scriptPath);
            if (fileExists) return false;

            if (!AssetDatabase.IsValidFolder("Assets/Scripts/UnityViewBinding"))
            {
                AssetDatabase.CreateFolder("Assets/Scripts", "UnityViewBinding");
            }

            File.WriteAllText(scriptPath, stringBuilder.ToString());
            AssetDatabase.Refresh();

            EditorPrefs.SetString(AssetPath, prefabStage.assetPath);
            EditorPrefs.SetBool(ShouldGenerateViewBinding, true);

            return true;
        }

        [UnityEditor.Callbacks.DidReloadScripts(-1)]
        private static void OnScriptsReloaded() => CreateViewBinding();

        private static void CreateViewBinding()
        {
            try
            {
                var shouldGenerateViewBinding = EditorPrefs.GetBool(ShouldGenerateViewBinding);
                var assetPath = EditorPrefs.GetString(AssetPath);

                if (!shouldGenerateViewBinding || assetPath == string.Empty)
                    return;

                var settings = ViewBinderSettings.GetOrCreateSettings();
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                var parsedName = $"{Normalize(asset.name)}Binding";
                MaybeAddComponent(parsedName, asset);

                var monos = new List<MonoBehaviour>();
                // we traverse all children MonoBehaviour and add based on following conditions:
                // if object is a direct child of "asset" we add it, no questions asked
                // if object is part of a prefab and is the outermost object
                // if object is part of a prefab and is not the outermost but the root prefab saved on disk
                // is not a .View one
                foreach (var mono in asset.GetComponentsInChildren<MonoBehaviour>())
                {
                    if (mono == null) continue;
                    if (mono.GetType().Name == parsedName) continue;
                    // ReSharper disable once Unity.UnknownTag
                    if (mono.gameObject.CompareTag(IgnoreTagName)) continue;

                    var prefabInstance = PrefabUtility.GetCorrespondingObjectFromSource(mono);
                    // part of the root asset and not inner prefab
                    if (prefabInstance == null)
                    {
                        monos.Add(mono);
                    }
                    // part of inner prefab and is outermost prefab
                    else if (prefabInstance != null && PrefabUtility.IsOutermostPrefabInstanceRoot(mono.gameObject))
                    {
                        monos.Add(mono);
                    }
                    // part of inner prefab and not outermost prefab and original prefab (saved on disk) name does not contain .view
                    else if (prefabInstance != null
                             && !PrefabUtility.IsOutermostPrefabInstanceRoot(mono.gameObject)
                             && !PrefabUtility.GetOriginalSourceRootWhereGameObjectIsAdded(mono.gameObject).name
                                 .EndsWith(settings.prefabSuffix, true, CultureInfo.InvariantCulture))
                    {
                        monos.Add(mono);
                    }
                }

                ValidateNoRepeatedFieldNames(monos);
                WriteViewBinderFile(parsedName, monos, asset);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
            finally
            {
                EditorPrefs.SetString(AssetPath, string.Empty);
                EditorPrefs.SetBool(ShouldGenerateViewBinding, false);
            }
        }

        private static void MaybeAddComponent(string parsedName, GameObject asset)
        {
            var settings = ViewBinderSettings.GetOrCreateSettings();
            var componentType =
                Type.GetType(
                    $"{settings.generatedNamespace}.{parsedName}, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
            if (asset.GetComponent(componentType) == null)
            {
                asset.AddComponent(componentType);
            }

            EditorUtility.SetDirty(asset);
        }

        private static void WriteViewBinderFile(string parsedName, List<MonoBehaviour> monos, GameObject asset)
        {
            const string indent = "    ";
            var settings = ViewBinderSettings.GetOrCreateSettings();
            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine("// This class was automatically generated by UnityViewBinding. Do not change.");
            stringBuilder.AppendLine($"namespace {settings.generatedNamespace}\n{{");
            stringBuilder.AppendLine($"public partial class {parsedName} \n{{");

            foreach (var mono in monos)
            {
                var type = mono.GetType();
                var fieldName = $"{Normalize(mono.name)}_{type.Name}";
                stringBuilder.AppendLine($"{indent}[UnityEngine.SerializeField] public {type.FullName} {fieldName};");
            }

            stringBuilder.AppendLine("\n#if UNITY_EDITOR");
            stringBuilder.AppendLine($"{indent}private void OnValidate()\n{indent}{{");
            stringBuilder.AppendLine($"{indent}{indent}var comps = GetComponentsInChildren<UnityEngine.MonoBehaviour>();");
            stringBuilder.AppendLine($"{indent}{indent}foreach (var component in comps) \n{indent}{indent}{{");

            foreach (var mono in monos)
            {
                var type = mono.GetType();
                var fieldName = $"{Normalize(mono.name)}_{type.Name}";
                stringBuilder.AppendLine(
                    $@"{indent}{indent}{indent}if (component.gameObject.name == ""{mono.name}"" && component is {type.FullName} {fieldName.ToLowerInvariant()}) {{");
                stringBuilder.AppendLine($"{indent}{indent}{indent}{indent}{fieldName} = {fieldName.ToLowerInvariant()};");
                stringBuilder.AppendLine($"{indent}{indent}{indent}}}");
            }

            stringBuilder.AppendLine($"{indent}{indent}}}");
            stringBuilder.AppendLine($"{indent}{indent}UnityEditor.EditorUtility.SetDirty(this);");
            stringBuilder.AppendLine($"{indent}}}");
            stringBuilder.AppendLine("#endif");
            stringBuilder.AppendLine("}");
            stringBuilder.AppendLine("}");

            var scriptPath = $"Assets/Scripts/UnityViewBinding/{Normalize(asset.name)}.Binding.cs";
            if (!AssetDatabase.IsValidFolder("Assets/Scripts/UnityViewBinding"))
            {
                AssetDatabase.CreateFolder("Assets/Scripts", "UnityViewBinding");
            }

            File.WriteAllText(scriptPath, stringBuilder.ToString());
            AssetDatabase.Refresh();
        }

        private static void ValidateNoRepeatedFieldNames(List<MonoBehaviour> monos)
        {
            var ids = new HashSet<string>();
            foreach (var mono in monos)
            {
                var type = mono.GetType();
                var fieldName = $"{Normalize(mono.name)}_{type.Name}";
                // ReSharper disable once CanSimplifySetAddingWithSingleCall
                if (ids.Contains(fieldName))
                {
                    throw new InvalidDataException(
                        $"Element id {fieldName} is already defined. GameObjects names are treated as Ids");
                }

                ids.Add(fieldName);
            }
        }

        private static string Normalize(string inStr) => Regex.Replace(inStr, @"[^\w]", "", RegexOptions.None, TimeSpan.FromSeconds(.5));
    }
}
