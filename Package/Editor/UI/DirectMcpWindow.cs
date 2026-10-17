using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DirectMcp
{
    public sealed class DirectMcpWindow : EditorWindow
    {
        static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        [MenuItem("Window/Direct MCP")]
        public static void Open()
        {
            var w = GetWindow<DirectMcpWindow>("Direct MCP");
            w.minSize = new Vector2(360, 320);
        }

        [InitializeOnLoadMethod]
        static void OpenOnFirstInstall()
        {
            const string key = "DirectMcp.WelcomeShown";
            if (EditorPrefs.GetBool(key, false)) return;
            EditorPrefs.SetBool(key, true);
            EditorApplication.delayCall += Open;
        }

        void OnInspectorUpdate() => Repaint();

        Vector2 _scroll;
        string _flash;
        double _flashUntil;

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawStatus();
            EditorGUILayout.Space(8);
            DrawConnect();
            EditorGUILayout.Space(8);
            DrawActivity();
            EditorGUILayout.EndScrollView();
        }

        void DrawStatus()
        {
            bool running = McpServer.IsRunning;
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                var dot = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = running ? new Color(0.25f, 0.8f, 0.35f) : new Color(0.9f, 0.3f, 0.3f) }, fontSize = 18 };
                GUILayout.Label("●", dot, GUILayout.Width(22));
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Label(running ? "Server running" : "Server stopped", EditorStyles.boldLabel);
                    GUILayout.Label(running ? McpServer.Url : (McpServer.LastError ?? "Click Start"), EditorStyles.miniLabel);
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(running ? "Stop" : "Start", GUILayout.Width(60), GUILayout.Height(30)))
                {
                    Settings.Enabled = !running;
                    if (running) McpServer.Stop(); else McpServer.Start();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                int port = EditorGUILayout.IntField("Port", Settings.Port);
                if (EditorGUI.EndChangeCheck() && port > 1024 && port < 65535)
                {
                    Settings.Port = port;
                }
                if (GUILayout.Button("Apply", GUILayout.Width(60))) McpServer.Restart();
            }
        }

        void DrawConnect()
        {
            GUILayout.Label("Connect your AI", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("One click writes the config into this project. Then open the AI tool in this project folder.", MessageType.None);

            Row("Claude Code", ".mcp.json", () => WriteConfig(".mcp.json", "mcpServers", new JObject { ["type"] = "http", ["url"] = McpServer.Url }));
            Row("Cursor", ".cursor/mcp.json", () => WriteConfig(Path.Combine(".cursor", "mcp.json"), "mcpServers", new JObject { ["url"] = McpServer.Url }));
            Row("VS Code (Copilot)", ".vscode/mcp.json", () => WriteConfig(Path.Combine(".vscode", "mcp.json"), "servers", new JObject { ["type"] = "http", ["url"] = McpServer.Url }));

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Copy URL")) Copy(McpServer.Url);
                if (GUILayout.Button("Copy 'claude mcp add' command")) Copy($"claude mcp add --transport http unity {McpServer.Url}");
            }

            if (_flash != null && EditorApplication.timeSinceStartup < _flashUntil)
                EditorGUILayout.HelpBox(_flash, MessageType.Info);
        }

        void Row(string label, string file, Action write)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool configured = IsConfigured(file);
                GUILayout.Label((configured ? "✓ " : "   ") + label, GUILayout.Width(150));
                GUILayout.Label(file, EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(configured ? "Update" : "Connect", GUILayout.Width(80))) write();
            }
        }

        static bool IsConfigured(string relative)
        {
            var path = Path.Combine(ProjectRoot, relative);
            return File.Exists(path) && File.ReadAllText(path).Contains(McpServer.Url);
        }

        void WriteConfig(string relative, string rootKey, JObject entry)
        {
            var path = Path.Combine(ProjectRoot, relative);
            JObject json;
            try { json = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject(); }
            catch (Exception e)
            {
                Flash($"{relative} is not valid JSON, not touching it: {e.Message}");
                return;
            }
            if (!(json[rootKey] is JObject servers)) json[rootKey] = servers = new JObject();
            servers["unity"] = entry;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, json.ToString(Formatting.Indented));
            Flash($"Wrote {relative}. Restart the AI tool (or run /mcp in Claude Code) to pick it up.");
        }

        void Copy(string text)
        {
            EditorGUIUtility.systemCopyBuffer = text;
            Flash("Copied: " + text);
        }

        void Flash(string text)
        {
            _flash = text;
            _flashUntil = EditorApplication.timeSinceStartup + 6;
        }

        void DrawActivity()
        {
            GUILayout.Label($"AI activity ({ActivityLog.TotalCalls} calls)", EditorStyles.boldLabel);
            var entries = ActivityLog.Snapshot();
            if (entries.Length == 0)
            {
                GUILayout.Label("No calls yet.", EditorStyles.miniLabel);
                return;
            }
            for (int i = entries.Length - 1; i >= 0; i--)
            {
                var e = entries[i];
                GUILayout.Label($"{e.Time:HH:mm:ss}  {(e.Ok ? "✓" : "✗")}  {e.Tool}  ({e.Ms} ms)", EditorStyles.miniLabel);
            }
        }
    }
}
