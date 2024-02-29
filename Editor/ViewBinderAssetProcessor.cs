namespace Editor
{
    using System.Text;
    using System.Text.RegularExpressions;
    using UnityEditor;
    using UnityEngine;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using UnityEditor.SceneManagement;

    [InitializeOnLoad]
    public class ViewBinderAssetProcessor
    {
        private const string ViewBindingAssetSuffix = ".view.prefab";
        private const string PartGroupPrefix = "---";
        private const string PartHeaderPrefix = "!u!";
        private const string GameObjectId = "1";
        private const string MonoBehaviourId = "114";
        private const string PrefabInstanceId = "1001";
        private const string Indentation = "  ";
        private const string ComponentPrefix = "  - component: {fileID: ";
        private const string ScriptPrefix = " {fileID: 11500000, guid:";
        private const string ScriptSuffix = ", type: 3}";

        static ViewBinderAssetProcessor()
        {
            PrefabStage.prefabStageClosing += OnPrefabUpdated;
        }

        private static void OnPrefabUpdated(PrefabStage prefabStage)
        {
            if (!EditorUtility.IsDirty(prefabStage.prefabContentsRoot))
            {
                Debug.Log("Asset not dirty, skipping");
                return;
            }
            if (!prefabStage.assetPath.ToLowerInvariant().EndsWith(ViewBindingAssetSuffix))
            {
                Debug.LogError("Asset name does not match format, skipping");
                return;
            }

            try
            {
                if (CreateMono(prefabStage))
                    return;
                
                EditorPrefs.SetString("AssetPath", prefabStage.assetPath);
                EditorPrefs.SetBool("ShouldGenerateViewBinding", true);
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
            var assetName = prefabStage.prefabContentsRoot.name;
            var parsedName = $"{Normalize(assetName)}Binding";
            var stringBuilder = new StringBuilder();
            stringBuilder.Append($@"using UnityEngine;
public partial class {parsedName} : MonoBehaviour {{}}
");

            var scriptPath = $"Assets/Scripts/UnityViewBinding/{Normalize(assetName)}.Mono.cs";
            var fileExists = File.Exists(scriptPath);
            if (fileExists) return false;

            if (!AssetDatabase.IsValidFolder("Assets/Scripts/UnityViewBinding"))
            {
                AssetDatabase.CreateFolder("Assets/Scripts", "UnityViewBinding");
            }

            File.WriteAllText(scriptPath, stringBuilder.ToString());
            AssetDatabase.Refresh();

            EditorPrefs.SetString("AssetPath", prefabStage.assetPath);
            EditorPrefs.SetBool("ShouldGenerateViewBinding", true);

            return true;
        }

        [UnityEditor.Callbacks.DidReloadScripts(-1)]
        private static void OnScriptsReloaded()
        {
            CreateViewBinding();
        }
        private static void CreateViewBinding()
        {
            try
            {
                var shouldGenerateViewBinding = EditorPrefs.GetBool("ShouldGenerateViewBinding");
                var assetPath = EditorPrefs.GetString("AssetPath");

                if (!shouldGenerateViewBinding || assetPath == string.Empty)
                    return;

                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                var parsedName = $"{Normalize(asset.name)}Binding";
                var componentType = Type.GetType($"{parsedName}, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
                if (asset.GetComponent(componentType) == null)
                {
                    asset.AddComponent(componentType);
                }
                EditorUtility.SetDirty(asset);

                ProcessAsset(assetPath);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
            finally
            {
                EditorPrefs.SetString("AssetPath", string.Empty);
                EditorPrefs.SetBool("ShouldGenerateViewBinding", false);
            }
        }

        private static void ProcessAsset(string path) => ReadAssetYaml(path);

        private static string Normalize(string inStr) => Regex.Replace(inStr, @"[^\w]", "", RegexOptions.None, TimeSpan.FromSeconds(.5));

        private static void ReadAssetYaml(string path)
        {
            var assetName = Path.GetFileNameWithoutExtension(path);
            var parsedName = $"{Normalize(assetName)}Binding";
            var groups = ParseYamlIntoGroups(path);

            FindProperties(groups, assetName, parsedName, out var properties, out var imports, out var propMappings);
            WriteCSharpFile(imports, parsedName, assetName, properties, propMappings);
        }

        private static void WriteCSharpFile(
            IEnumerable<string> imports,
            string parsedName,
            string assetName,
            IEnumerable<string> properties,
            List<PartMapping> propMappings)
        {
            var stringBuilder = new StringBuilder();
            stringBuilder.Append(@$"// This class was automatically generated by UnityViewBinding. Do not change.

using UnityEngine;
{string.Join(";\n", imports)};

using TMP = TMPro.TextMeshProUGUI;

public partial class {parsedName} 
{{

{string.Join(";\n", properties)};
#if UNITY_EDITOR
    private void OnValidate()
    {{
        var comps = GetComponentsInChildren<MonoBehaviour>();
        foreach (var component in comps)
        {{");

            foreach (var map in propMappings)
            {
                stringBuilder.Append($@"
            if (component.gameObject.name == ""{map.ObjectName}"" && component is {map.ClassName} {map.PropertyName.ToLowerInvariant()})
            {{
                {map.PropertyName} = {map.PropertyName.ToLowerInvariant()};
            }}
");
            }

            stringBuilder.Append($@"
        }}
        UnityEditor.EditorUtility.SetDirty(this);
    }}
#endif

}}");
            var scriptPath = $"Assets/Scripts/UnityViewBinding/{Normalize(assetName)}.Binding.cs";
            if (!AssetDatabase.IsValidFolder("Assets/Scripts/UnityViewBinding"))
            {
                AssetDatabase.CreateFolder("Assets/Scripts", "UnityViewBinding");
            }

            File.WriteAllText(scriptPath, stringBuilder.ToString());
            AssetDatabase.Refresh();
        }

        private static void FindProperties(
            Dictionary<PartDefinition, List<PartProperty>> groups,
            string assetName,
            string parsedClassName,
            out HashSet<string> properties,
            out HashSet<string> imports,
            out List<PartMapping> propMappings)
        {
            var ids = new HashSet<string>();
            imports = new HashSet<string>();
            properties = new HashSet<string>();
            propMappings = new List<PartMapping>();

            foreach (var (partDefinition, partProperties) in groups)
            {
                switch (partDefinition.PartType)
                {
                    case GameObjectId:
                        ProcessGameObjectNode(groups, assetName, parsedClassName, properties, imports, propMappings, partProperties, ids);
                        break;
                    case PrefabInstanceId:
                        ProcessPrefabNode(properties, propMappings, partProperties, ids);
                        break;
                }
            }
        }

        private static void ProcessPrefabNode(ISet<string> properties,
            ICollection<PartMapping> propMappings,
            IList<PartProperty> partProperties,
            ISet<string> ids)
        {
            var propNameKey = partProperties.First(it => it.PropertyValue == "m_Name");
            var propNameKeyIndex = partProperties.IndexOf(propNameKey);
            var prefabInstanceName = partProperties[propNameKeyIndex + 1].PropertyValue;

            var prefabGuidValue = partProperties
                .First(it => it.PropertyName == "m_SourcePrefab")
                .PropertyValue;
            const string guidKey = "guid: ";
            var prefabGuid = prefabGuidValue.Substring(prefabGuidValue.IndexOf(guidKey, StringComparison.Ordinal) + guidKey.Length);
            prefabGuid = prefabGuid[..prefabGuid.IndexOf(',')];

            var prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuid);
            var className = Normalize(Path.GetFileNameWithoutExtension(prefabPath)) + "Binding";

            var property = $"    [SerializeField] public {className} {Normalize(prefabInstanceName)}";

            // ReSharper disable once CanSimplifySetAddingWithSingleCall
            if (ids.Contains(prefabInstanceName))
            {
                throw new InvalidDataException(
                $"Element id {prefabInstanceName} is already defined. GameObjects names are treated as Ids");
            }

            ids.Add(prefabInstanceName);

            properties.Add(property);
            propMappings.Add(new PartMapping
            {
                ClassName = className,
                ObjectName = prefabInstanceName,
                PropertyName = Normalize(prefabInstanceName)
            });
        }

        private static void ProcessGameObjectNode(
            Dictionary<PartDefinition, List<PartProperty>> groups,
            string assetName,
            string parsedClassName,
            ISet<string> properties,
            ISet<string> imports,
            ICollection<PartMapping> propMappings,
            IList<PartProperty> partProperties,
            ISet<string> ids
        )
        {
            if (partProperties == null) throw new ArgumentNullException(nameof(partProperties));
            var originalName = partProperties.First(it => it.PropertyName == "m_Name").PropertyValue;
            var name = originalName;
            if (name == assetName)
            {
                name = "Root";
            }

            name = Normalize(name);

            var monoScriptReferences = FindMonoScripts(partProperties, groups);
            foreach (var monoScriptReference in monoScriptReferences)
            {
                var scriptPath = AssetDatabase.GUIDToAssetPath(monoScriptReference.AssetGuid);
                var behaviourName = Path.GetFileNameWithoutExtension(scriptPath);

                if (behaviourName.StartsWith(Normalize(originalName)))
                    continue;

                if (behaviourName == "TextMeshProUGUI")
                    behaviourName = "TMP";

                var behaviourNamespace =
                    File.ReadAllLines(scriptPath).FirstOrDefault(it => it.StartsWith("namespace"))?["namespace".Length..]?.Trim();
                var propName = $"{name}_{behaviourName}";
                var property = $"    [SerializeField] public {behaviourName} {propName}";

                // ReSharper disable once CanSimplifySetAddingWithSingleCall
                if (ids.Contains(propName))
                {
                    throw new InvalidDataException(
                    $"Element id {propName} is already defined. GameObjects names are treated as Ids");
                }

                ids.Add(propName);
                properties.Add(property);
                if (behaviourNamespace is not null)
                {
                    imports.Add($"using {behaviourNamespace}");
                }

                propMappings.Add(new PartMapping
                {
                    ClassName = behaviourName,
                    ObjectName = originalName,
                    PropertyName = propName
                });
            }
        }

        private static Dictionary<PartDefinition, List<PartProperty>> ParseYamlIntoGroups(string path)
        {
            var groupsRaw = File.ReadAllText(path).Split(PartGroupPrefix).ToList();
            var groups = new Dictionary<PartDefinition, List<PartProperty>>();

            foreach (var group in groupsRaw)
            {
                PartDefinition partDefinition = default;
                var partProperties = new List<PartProperty>();

                foreach (var line in group.Split('\n'))
                {
                    // discard file headers
                    if (line.StartsWith('%')) continue;

                    // Group (MonoBehaviour/GameObject/Transform/etc) heading
                    // --- !u!1 &1753319643823132500
                    if (line.Trim().StartsWith(PartHeaderPrefix))
                    {
                        partDefinition = new PartDefinition
                        {
                            PartType = line[4..].Split('&')[0].Trim(),
                            FileId = line[(line.IndexOf('&') + 1)..].Trim(),
                        };
                    }

                    // looking first for a single indentation property
                    if (line.StartsWith($"{Indentation}"))
                    {
                        var colonIndex = line.IndexOf(':');
                        if (line.StartsWith(ComponentPrefix))
                        {
                            var fileID = line.Substring(ComponentPrefix.Length,
                            line.IndexOf('}') - ComponentPrefix.Length).Trim();
                            partProperties.Add(new PartComponent
                            {
                                FileId = fileID
                            });
                        }
                        else
                        {
                            partProperties.Add(new()
                            {
                                PropertyName = line[..colonIndex].Trim(),
                                PropertyValue = line[(colonIndex + 1)..].Trim(),
                            });
                        }
                    }
                }

                if (partDefinition == null) continue;

                groups[partDefinition] = partProperties;
            }

            return groups;
        }

        /// <summary>
        /// Returns tuple of (fileId, guid)
        /// </summary>
        /// <param name="partProperties"></param>
        /// <param name="groups"></param>
        /// <returns></returns>
        private static List<MonoScriptReference> FindMonoScripts(
            IEnumerable<PartProperty> partProperties,
            Dictionary<PartDefinition, List<PartProperty>> groups)
        {
            var monoReferences = new List<MonoScriptReference>();
            // a GameObject in the yaml has a field called "components"
            // which points to another component in the yaml using the fileID
            var components = partProperties.OfType<PartComponent>().Select(it => it.FileId).ToList();

            foreach (var componentFileId in components)
            {
                var component = groups.First(it => it.Key.FileId == componentFileId);

                if (component.Key.PartType == MonoBehaviourId)
                {
                    var script = component.Value
                        .First(it => it.PropertyName == "m_Script")
                        .PropertyValue;
                    var monoScript = new MonoScriptReference
                    {
                        FileId = component.Key.FileId.Trim(),
                        AssetGuid = script.Substring(ScriptPrefix.Length,
                        script.IndexOf(ScriptSuffix, StringComparison.Ordinal) - ScriptPrefix.Length).Trim(),
                    };
                    monoReferences.Add(monoScript);
                }
            }

            return monoReferences;
        }
    }
}

public class PartDefinition
{
    public string PartType;
    public string FileId;
}

public class PartProperty
{
    public string PropertyName;
    public string PropertyValue;
}

public class PartComponent : PartProperty
{
    // - component: {fileID: n}
    public string FileId;
}

public class MonoScriptReference
{
    public string FileId;
    public string AssetGuid;
}

public class PartMapping
{
    public string PropertyName;
    public string ClassName;
    public string ObjectName;
}