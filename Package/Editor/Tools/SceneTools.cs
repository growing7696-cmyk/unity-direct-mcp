using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace DirectMcp.Tools
{
    /// <summary>Hierarchy paths like "Environment/Props/Crate" (inactive objects included).</summary>
    public static class ObjectPaths
    {
        public static string PathOf(GameObject go)
        {
            var parts = new List<string>();
            for (var t = go.transform; t != null; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        public static IEnumerable<GameObject> Roots()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                foreach (var r in s.GetRootGameObjects()) yield return r;
            }
            if (Application.isPlaying)
            {
                // Objects moved to DontDestroyOnLoad live in a hidden scene.
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                    if (go.scene.name == "DontDestroyOnLoad" && go.transform.parent == null) yield return go;
            }
        }

        public static GameObject Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            path = path.Trim().TrimStart('/');
            int slash = path.IndexOf('/');
            string rootName = slash < 0 ? path : path.Substring(0, slash);
            foreach (var root in Roots())
            {
                if (root.name != rootName) continue;
                if (slash < 0) return root;
                var child = root.transform.Find(path.Substring(slash + 1));
                if (child != null) return child.gameObject;
            }
            return null;
        }
    }

    public static class SceneTools
    {
        [McpTool("scene_hierarchy",
            "Tree of GameObjects in the open scenes with their components, e.g. 'Player [Rigidbody, PlayerController]'. " +
            "Use the printed paths (Parent/Child) with inspect_object and in execute_csharp (GameObject.Find does not find inactive objects).",
            InputSchema = @"{""type"":""object"",""properties"":{
                ""root"":{""type"":""string"",""description"":""Only show this object and its children (hierarchy path).""},
                ""max_depth"":{""type"":""integer"",""description"":""Default 6.""},
                ""components"":{""type"":""boolean"",""description"":""List component types. Default true.""},
                ""max_objects"":{""type"":""integer"",""description"":""Default 400.""}}}")]
        public static ToolResult Hierarchy(JObject args)
        {
            int maxDepth = (int?)args["max_depth"] ?? 6;
            bool comps = (bool?)args["components"] ?? true;
            int maxObjects = (int?)args["max_objects"] ?? 400;
            var rootPath = (string)args["root"];

            var sb = new StringBuilder();
            int count = 0;
            bool truncated = false;

            void Walk(Transform t, int depth)
            {
                if (count >= maxObjects) { truncated = true; return; }
                count++;
                var go = t.gameObject;
                sb.Append(' ', depth * 2).Append("- ").Append(go.name);
                if (!go.activeSelf) sb.Append(" (inactive)");
                if (comps)
                {
                    var names = go.GetComponents<Component>()
                        .Where(c => !(c is Transform))
                        .Select(c => c == null ? "MissingScript" : c.GetType().Name)
                        .ToArray();
                    if (names.Length > 0) sb.Append(" [").Append(string.Join(", ", names)).Append(']');
                }
                if (depth >= maxDepth && t.childCount > 0)
                {
                    sb.Append(" (+").Append(t.childCount).AppendLine(" children)");
                    return;
                }
                sb.AppendLine();
                foreach (Transform child in t) Walk(child, depth + 1);
            }

            if (!string.IsNullOrEmpty(rootPath))
            {
                var go = ObjectPaths.Find(rootPath);
                if (go == null) return ToolResult.Error($"No GameObject at path '{rootPath}'.");
                sb.AppendLine(ObjectPaths.PathOf(go));
                Walk(go.transform, 0);
            }
            else
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    sb.Append("Scene: ").Append(s.name).Append(" (").Append(string.IsNullOrEmpty(s.path) ? "unsaved" : s.path).Append(')');
                    if (s.isDirty) sb.Append(" *modified*");
                    sb.AppendLine();
                    if (!s.isLoaded) continue;
                    foreach (var r in s.GetRootGameObjects()) Walk(r.transform, 0);
                }
            }
            if (truncated) sb.AppendLine($"... truncated at {maxObjects} objects. Use root/max_depth to narrow down.");
            return ToolResult.Text(sb.ToString());
        }

        [McpTool("inspect_object",
            "Show a GameObject (by hierarchy path) or an asset (by path like Assets/Prefabs/Enemy.prefab): transform, " +
            "components and their serialized fields with current values. Use this before changing anything.",
            InputSchema = @"{""type"":""object"",""required"":[""target""],""properties"":{
                ""target"":{""type"":""string"",""description"":""Hierarchy path (Player/Weapon) or asset path (Assets/...).""},
                ""component"":{""type"":""string"",""description"":""Only show components whose type name contains this.""}}}")]
        public static ToolResult Inspect(JObject args)
        {
            var target = (string)args["target"];
            var filter = (string)args["component"];
            Object obj;
            if (target != null && target.StartsWith("Assets/", StringComparison.Ordinal) || target != null && target.StartsWith("Packages/", StringComparison.Ordinal))
            {
                obj = AssetDatabase.LoadMainAssetAtPath(target);
                if (obj == null) return ToolResult.Error($"No asset at '{target}'.");
            }
            else
            {
                obj = ObjectPaths.Find(target);
                if (obj == null) return ToolResult.Error($"No GameObject at path '{target}'. Call scene_hierarchy to see valid paths.");
            }

            var sb = new StringBuilder();
            if (obj is GameObject go)
            {
                sb.Append("GameObject: ").AppendLine(go.scene.IsValid() ? ObjectPaths.PathOf(go) : target);
                sb.Append("active: ").Append(go.activeSelf).Append("  tag: ").Append(go.tag)
                  .Append("  layer: ").Append(LayerMask.LayerToName(go.layer)).Append("  static: ").Append(go.isStatic).AppendLine();
                if (PrefabUtility.IsPartOfPrefabInstance(go))
                    sb.Append("prefab: ").AppendLine(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go));
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) { sb.AppendLine("\n[Missing Script]"); continue; }
                    if (!string.IsNullOrEmpty(filter) && c.GetType().Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    AppendObject(sb, c);
                }
                if (go.transform.childCount > 0)
                {
                    sb.Append("\nchildren: ");
                    sb.AppendLine(string.Join(", ", go.transform.Cast<Transform>().Select(t => t.name)));
                }
            }
            else
            {
                AppendObject(sb, obj);
            }
            return ToolResult.Text(sb.ToString());
        }

        static void AppendObject(StringBuilder sb, Object o)
        {
            sb.AppendLine().Append('[').Append(o.GetType().Name).Append(']');
            if (o is Behaviour b && !b.enabled) sb.Append(" (disabled)");
            sb.AppendLine();
            if (o is Transform t)
            {
                sb.Append("  localPosition: ").AppendLine(Fmt(t.localPosition));
                sb.Append("  localRotation(euler): ").AppendLine(Fmt(t.localEulerAngles));
                sb.Append("  localScale: ").AppendLine(Fmt(t.localScale));
                sb.Append("  worldPosition: ").AppendLine(Fmt(t.position));
                return;
            }

            var so = new SerializedObject(o);
            var it = so.GetIterator();
            int shown = 0;
            bool enter = true;
            while (it.NextVisible(enter))
            {
                enter = it.propertyType == SerializedPropertyType.Generic && !it.isArray && it.depth < 2;
                if (it.name == "m_Script" || it.name == "m_ObjectHideFlags") continue;
                if (++shown > 80) { sb.AppendLine("  ..."); break; }
                sb.Append(' ', 2 + it.depth * 2).Append(it.name).Append(": ").AppendLine(Value(it));
            }
        }

        static string Value(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.longValue.ToString();
                case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float: return p.floatValue.ToString("0.###");
                case SerializedPropertyType.String: return "\"" + p.stringValue + "\"";
                case SerializedPropertyType.Color: return p.colorValue.ToString();
                case SerializedPropertyType.ObjectReference:
                    var r = p.objectReferenceValue;
                    if (r == null) return "None";
                    var path = AssetDatabase.GetAssetPath(r);
                    return $"{r.GetType().Name} '{r.name}'" + (string.IsNullOrEmpty(path) ? "" : $" ({path})");
                case SerializedPropertyType.LayerMask: return p.intValue.ToString();
                case SerializedPropertyType.Enum:
                    return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumDisplayNames.Length ? p.enumDisplayNames[p.enumValueIndex] : p.intValue.ToString();
                case SerializedPropertyType.Vector2: return Fmt(p.vector2Value);
                case SerializedPropertyType.Vector3: return Fmt(p.vector3Value);
                case SerializedPropertyType.Vector4: return p.vector4Value.ToString();
                case SerializedPropertyType.Rect: return p.rectValue.ToString();
                case SerializedPropertyType.Bounds: return p.boundsValue.ToString();
                case SerializedPropertyType.Quaternion: return Fmt(p.quaternionValue.eulerAngles) + " (euler)";
                case SerializedPropertyType.Vector2Int: return p.vector2IntValue.ToString();
                case SerializedPropertyType.Vector3Int: return p.vector3IntValue.ToString();
                case SerializedPropertyType.AnimationCurve: return $"AnimationCurve ({p.animationCurveValue?.length ?? 0} keys)";
                default:
                    if (p.isArray) return $"array[{p.arraySize}]";
                    return p.hasVisibleChildren ? "" : p.type;
            }
        }

        static string Fmt(Vector3 v) => $"({v.x:0.###}, {v.y:0.###}, {v.z:0.###})";
        static string Fmt(Vector2 v) => $"({v.x:0.###}, {v.y:0.###})";

        [McpTool("screenshot",
            "Render a camera to a PNG and return it so you can see the result. view='game' uses the main camera " +
            "(or the camera named in 'camera'); view='scene' uses the Scene view camera. Screen-space-overlay UI is not included.",
            InputSchema = @"{""type"":""object"",""properties"":{
                ""view"":{""type"":""string"",""enum"":[""game"",""scene""],""description"":""Default game.""},
                ""camera"":{""type"":""string"",""description"":""Hierarchy path of a specific camera.""},
                ""width"":{""type"":""integer"",""description"":""Default 960, max 1920.""},
                ""height"":{""type"":""integer"",""description"":""Default 540, max 1080.""}}}")]
        public static ToolResult Screenshot(JObject args)
        {
            int w = Mathf.Clamp((int?)args["width"] ?? 960, 64, 1920);
            int h = Mathf.Clamp((int?)args["height"] ?? 540, 64, 1080);
            var view = (string)args["view"] ?? "game";
            var camPath = (string)args["camera"];

            Camera cam;
            if (!string.IsNullOrEmpty(camPath))
            {
                cam = ObjectPaths.Find(camPath)?.GetComponent<Camera>();
                if (cam == null) return ToolResult.Error($"No Camera at '{camPath}'.");
            }
            else if (view == "scene")
            {
                cam = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
                if (cam == null) return ToolResult.Error("No Scene view is open.");
            }
            else
            {
                cam = Camera.main ?? Camera.allCameras.OrderByDescending(c => c.depth).FirstOrDefault();
                if (cam == null) return ToolResult.Error("No enabled camera in the scene. Add one, or use view='scene'.");
            }

            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                var png = tex.EncodeToPNG();
                var t = cam.transform;
                return ToolResult.Text($"Camera '{ObjectPaths.PathOf(cam.gameObject)}' at {Fmt(t.position)} looking {Fmt(t.forward)}, {w}x{h}.")
                    .AddImage(png);
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
            }
        }
    }
}
