using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
            "See the game. In play mode the default captures the Game view exactly as the player sees it, UI included " +
            "(Canvas, IMGUI/OnGUI, UI Toolkit). In edit mode, or with 'camera', it renders that camera (default: main camera) " +
            "with Screen Space Overlay canvases drawn on top. view='scene' uses the Scene view camera.",
            InputSchema = @"{""type"":""object"",""properties"":{
                ""view"":{""type"":""string"",""enum"":[""game"",""scene""],""description"":""Default game.""},
                ""camera"":{""type"":""string"",""description"":""Hierarchy path of a specific camera.""},
                ""width"":{""type"":""integer"",""description"":""Default 960, max 1920. In play mode the image keeps the Game view's aspect ratio within width x height.""},
                ""height"":{""type"":""integer"",""description"":""Default 540, max 1080.""}}}")]
        public static ToolResult Screenshot(JObject args)
        {
            int w = Mathf.Clamp((int?)args["width"] ?? 960, 64, 1920);
            int h = Mathf.Clamp((int?)args["height"] ?? 540, 64, 1080);
            var shot = Capture.Take((string)args["view"] ?? "game", (string)args["camera"], w, h);
            if (shot.Error != null) return ToolResult.Error(shot.Error);
            return ToolResult.Text(shot.Info).AddImage(shot.Png);
        }
    }

    /// <summary>Screenshots for the screenshot and playtest tools. Call on the main thread.</summary>
    public static class Capture
    {
        public sealed class Shot
        {
            public byte[] Png;
            public string Info;
            public string Error;
        }

        public static Shot Take(string view, string cameraPath, int maxWidth, int maxHeight)
        {
            if (view != "scene" && string.IsNullOrEmpty(cameraPath) && EditorApplication.isPlaying)
            {
                var png = GameView(maxWidth, maxHeight, out var size);
                if (png != null) return new Shot { Png = png, Info = $"Game view as the player sees it (UI included), {size}." };
            }

            Camera cam;
            if (!string.IsNullOrEmpty(cameraPath))
            {
                cam = ObjectPaths.Find(cameraPath)?.GetComponent<Camera>();
                if (cam == null) return new Shot { Error = $"No Camera at '{cameraPath}'." };
            }
            else if (view == "scene")
            {
                cam = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
                if (cam == null) return new Shot { Error = "No Scene view is open." };
            }
            else
            {
                cam = Camera.main ?? Camera.allCameras.OrderByDescending(c => c.depth).FirstOrDefault();
                if (cam == null) return new Shot { Error = "No enabled camera in the scene. Add one, or use view='scene'." };
            }

            bool withUi = view != "scene";
            var t = cam.transform;
            return new Shot
            {
                Png = Render(cam, maxWidth, maxHeight, withUi, out int overlays),
                Info = $"Camera '{ObjectPaths.PathOf(cam.gameObject)}' at {Fmt(t.position)} looking {Fmt(t.forward)}, {maxWidth}x{maxHeight}" +
                       (overlays > 0 ? $", {overlays} overlay canvas(es) drawn on top." : "."),
            };
        }

        static Type _gameViewType;
        static MethodInfo _renderView;

        /// <summary>
        /// Renders the Game view through its own code path (PlayModeView.RenderView), which draws the cameras plus
        /// Canvas, IMGUI and UI Toolkit. That is internal Editor API, so any failure returns null and the caller
        /// falls back to rendering the camera.
        /// </summary>
        static byte[] GameView(int maxWidth, int maxHeight, out string size)
        {
            size = null;
            Texture2D tex = null;
            try
            {
                _gameViewType ??= typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                _renderView ??= _gameViewType?.BaseType?.GetMethod("RenderView", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(Vector2), typeof(bool) }, null);
                if (_renderView == null) return null;
                var views = Resources.FindObjectsOfTypeAll(_gameViewType);
                if (views.Length == 0) return null;
                if (!(_renderView.Invoke(views[0], new object[] { new Vector2(-1, -1), false }) is RenderTexture src) || src.width < 16 || src.height < 16)
                    return null;

                float scale = Mathf.Min(1f, Mathf.Min((float)maxWidth / src.width, (float)maxHeight / src.height));
                int w = Mathf.Max(16, Mathf.RoundToInt(src.width * scale));
                int h = Mathf.Max(16, Mathf.RoundToInt(src.height * scale));
                var dst = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                // The Game view texture is stored top-down on Metal, D3D and Vulkan.
                if (SystemInfo.graphicsUVStartsAtTop) Graphics.Blit(src, dst, new Vector2(1, -1), new Vector2(0, 1));
                else Graphics.Blit(src, dst);
                tex = ReadBack(dst, w, h);
                RenderTexture.ReleaseTemporary(dst);

                // A Game view hidden behind another tab is not rendered and comes back as one flat color.
                if (IsFlat(tex)) return null;
                size = $"{w}x{h}";
                return tex.EncodeToPNG();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (tex != null) Object.DestroyImmediate(tex);
            }
        }

        static byte[] Render(Camera cam, int w, int h, bool withOverlayUi, out int overlayCount)
        {
            // Screen Space Overlay canvases are drawn by the Game view, not by cameras, so render them through this
            // camera for the shot and put them back afterwards.
            var overlays = new List<(Canvas canvas, Camera worldCamera, float planeDistance)>();
            if (withOverlayUi)
                foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
                    if (c.gameObject.scene.IsValid() && c.isRootCanvas && c.isActiveAndEnabled && c.renderMode == RenderMode.ScreenSpaceOverlay)
                        overlays.Add((c, c.worldCamera, c.planeDistance));
            overlayCount = overlays.Count;

            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
            var prevTarget = cam.targetTexture;
            Texture2D tex = null;
            try
            {
                cam.targetTexture = rt;
                foreach (var o in overlays)
                {
                    o.canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    o.canvas.worldCamera = cam;
                    o.canvas.planeDistance = cam.nearClipPlane + 0.01f;
                }
                if (overlays.Count > 0) Canvas.ForceUpdateCanvases();
                cam.Render();
                tex = ReadBack(rt, w, h);
                return tex.EncodeToPNG();
            }
            finally
            {
                foreach (var o in overlays)
                {
                    o.canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    o.canvas.worldCamera = o.worldCamera;
                    o.canvas.planeDistance = o.planeDistance;
                }
                if (overlays.Count > 0) Canvas.ForceUpdateCanvases();
                cam.targetTexture = prevTarget;
                Object.DestroyImmediate(rt);
                if (tex != null) Object.DestroyImmediate(tex);
            }
        }

        static Texture2D ReadBack(RenderTexture rt, int w, int h)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex;
        }

        static bool IsFlat(Texture2D tex)
        {
            var first = tex.GetPixel(0, 0);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    if (tex.GetPixel(tex.width * (2 * x + 1) / 16, tex.height * (2 * y + 1) / 16) != first) return false;
            return true;
        }

        static string Fmt(Vector3 v) => $"({v.x:0.###}, {v.y:0.###}, {v.z:0.###})";
    }
}
