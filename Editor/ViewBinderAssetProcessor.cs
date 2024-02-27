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
            if (!EditorUtility.IsDirty(prefabStage.prefabContentsRoot)) return;
            if (!prefabStage.assetPath.ToLowerInvariant().EndsWith(ViewBindingAssetSuffix)) return;

            try
            {
                ProcessAsset(prefabStage.assetPath);
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

        private static void ProcessAsset(string path)
        {
            ReadAssetYaml(path);
        }

        private static string Normalize(string inStr)
        {
            return Regex.Replace(inStr, @"[^\w]", "", RegexOptions.None, TimeSpan.FromSeconds(.5));
        }

        private static void ReadAssetYaml(string path)
        {
            var assetName = Path.GetFileNameWithoutExtension(path);
            var parsedName = $"{Normalize(assetName)}Binding";
            var groups = ParseYamlIntoGroups(path);

            FindProperties(groups, assetName, parsedName, out var properties, out var imports, out var propMappings);
            WriteCSharpFile(imports, parsedName, properties, propMappings);
        }

        private static void WriteCSharpFile(
            IEnumerable<string> imports,
            string parsedName,
            IEnumerable<string> properties,
            List<PartMapping> propMappings)
        {
            var stringBuilder = new StringBuilder();
            stringBuilder.Append(@$"// This class was automatically generated by UnityViewBinding. Do not change.

using UnityEngine;
using UnityEditor;
{string.Join(";\n", imports)};

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
            if (component.gameObject.name == ""{map.objectName}"" && component is {map.className} {map.propertyName.ToLowerInvariant()})
            {{
                {map.propertyName} = {map.propertyName.ToLowerInvariant()};
            }}
");
            }

            stringBuilder.Append($@"
        }}
        EditorUtility.SetDirty(this);
    }}
#endif

}}");
            var scriptPath = $"Assets/Scripts/UnityViewBinding/{parsedName}.cs";
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
                className = className,
                objectName = prefabInstanceName,
                propertyName = Normalize(prefabInstanceName)
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

                if (behaviourName == parsedClassName)
                    continue;

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
                    className = behaviourName,
                    objectName = originalName,
                    propertyName = propName
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
    public string propertyName;
    public string className;
    public string objectName;
}