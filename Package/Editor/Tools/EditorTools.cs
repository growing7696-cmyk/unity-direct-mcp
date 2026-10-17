using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DirectMcp.Tools
{
    /// <summary>
    /// Play mode normally stops ticking when the Editor is not the focused app, which is exactly the situation when
    /// an agent drives it from a terminal: the game sits on frame 1. Keep it running for the play session.
    /// Application.runInBackground set here is a play-mode value and does not change Player Settings.
    /// </summary>
    [InitializeOnLoad]
    static class PlayInBackground
    {
        static PlayInBackground()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && Settings.Enabled) Application.runInBackground = true;
            };
        }
    }

    public static class EditorTools
    {
        [McpTool("unity_status",
            "Editor state: Unity version, open scenes, play mode, whether scripts are compiling, the last compile result " +
            "with errors, and console error/warning counts. Pass wait_seconds to wait until compiling/importing finishes " +
            "(use after refresh_assets or play_mode).",
            InputSchema = @"{""type"":""object"",""properties"":{
                ""wait_seconds"":{""type"":""number"",""description"":""Wait up to this many seconds (max 120) for compile/import/play-mode change to finish. Default 0.""}}}",
            MainThreadOnly = false)]
        public static ToolResult Status(JObject args)
        {
            double wait = Math.Min(120, Math.Max(0, (double?)args["wait_seconds"] ?? 0));
            bool reloading = MainThread.IsReloading;

            if (wait > 0 && !reloading)
            {
                var sw = Stopwatch.StartNew();
                int idlePolls = 0;
                while (sw.Elapsed.TotalSeconds < wait)
                {
                    bool busy;
                    try
                    {
                        busy = MainThread.Invoke(() =>
                            EditorApplication.isCompiling || EditorApplication.isUpdating ||
                            EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying);
                    }
                    catch (ReloadingException)
                    {
                        reloading = true;
                        break;
                    }
                    // Compilation can start a moment after a refresh, so require a short stable idle period.
                    idlePolls = busy ? 0 : idlePolls + 1;
                    if (idlePolls >= 4) break;
                    Thread.Sleep(250);
                }
            }

            JObject state;
            if (reloading)
            {
                state = new JObject
                {
                    ["reloading"] = true,
                    ["note"] = "Unity is reloading scripts. Call unity_status again in a few seconds.",
                };
            }
            else
            {
                try { state = MainThread.Invoke(Snapshot); }
                catch (ReloadingException)
                {
                    state = new JObject { ["reloading"] = true, ["note"] = "Unity is reloading scripts. Call unity_status again in a few seconds." };
                }
            }

            var compile = CompileState.Last;
            state["last_compile"] = new JObject
            {
                ["finished_at"] = compile.FinishedAt,
                ["succeeded"] = compile.Succeeded,
                ["errors"] = new JArray(compile.Errors),
                ["warning_count"] = compile.WarningCount,
            };
            var (errors, warnings) = LogBuffer.Counts();
            state["console"] = new JObject { ["errors"] = errors, ["warnings"] = warnings, ["hint"] = errors > 0 ? "call read_console" : null };
            return ToolResult.Text(state.ToString(Formatting.Indented));
        }

        static JObject Snapshot()
        {
            var scenes = new JArray();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                scenes.Add(new JObject { ["name"] = s.name, ["path"] = s.path, ["dirty"] = s.isDirty, ["loaded"] = s.isLoaded });
            }
            return new JObject
            {
                ["unity_version"] = Application.unityVersion,
                ["project"] = System.IO.Path.GetDirectoryName(Application.dataPath),
                ["render_pipeline"] = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null
                    ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.GetType().Name : "Built-in",
                ["scenes"] = scenes,
                ["play_mode"] = EditorApplication.isPlaying ? (EditorApplication.isPaused ? "paused" : "playing") : "editing",
                ["compiling"] = EditorApplication.isCompiling,
                ["importing"] = EditorApplication.isUpdating,
                ["selection"] = Selection.activeGameObject != null ? ObjectPaths.PathOf(Selection.activeGameObject) : null,
                ["play"] = EditorApplication.isPlaying
                    ? new JObject { ["frame"] = Time.frameCount, ["time"] = Math.Round(Time.time, 2), ["time_scale"] = Time.timeScale }
                    : null,
            };
        }

        [McpTool("refresh_assets",
            "Import changed files and recompile scripts. Call after creating or editing files under Assets/, then call " +
            "unity_status with wait_seconds=60 to get the compile result.",
            InputSchema = @"{""type"":""object"",""properties"":{}}")]
        public static ToolResult Refresh(JObject args)
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            CompilationPipeline.RequestScriptCompilation();
            return ToolResult.Text(
                "Refresh done. If scripts changed, Unity is compiling now. Next: unity_status with wait_seconds=60. " +
                "After a successful compile Unity reloads scripts for a few seconds.");
        }

        [McpTool("play_mode",
            "Enter, exit, pause or resume play mode. Entering reloads scripts: afterwards call unity_status with " +
            "wait_seconds=30, then screenshot / read_console to check the game.",
            InputSchema = @"{""type"":""object"",""required"":[""action""],""properties"":{
                ""action"":{""type"":""string"",""enum"":[""enter"",""exit"",""pause"",""resume""]}}}")]
        public static ToolResult PlayMode(JObject args)
        {
            var action = (string)args["action"];
            switch (action)
            {
                case "enter":
                    if (EditorApplication.isPlaying) return ToolResult.Text("Already in play mode.");
                    if (EditorUtility.scriptCompilationFailed)
                        return ToolResult.Error("Cannot enter play mode: scripts have compile errors. See unity_status.");
                    LogBuffer.Clear();
                    // Defer so this response is sent before the domain reload.
                    NextUpdate(() => EditorApplication.isPlaying = true);
                    return ToolResult.Text("Entering play mode (console cleared). Next: unity_status with wait_seconds=30, " +
                        "then playtest to let it run and watch values, or screenshot / read_console.");
                case "exit":
                    if (!EditorApplication.isPlaying) return ToolResult.Text("Not in play mode.");
                    NextUpdate(() => EditorApplication.isPlaying = false);
                    return ToolResult.Text("Exiting play mode.");
                case "pause":
                    EditorApplication.isPaused = true;
                    return ToolResult.Text("Paused.");
                case "resume":
                    EditorApplication.isPaused = false;
                    return ToolResult.Text("Resumed.");
                default:
                    return ToolResult.Error("action must be enter, exit, pause or resume.");
            }
        }

        /// <summary>
        /// Runs once on the next Editor update. EditorApplication.delayCall is not processed while the Editor is
        /// in the background, which is where it is when an agent drives it from a terminal.
        /// </summary>
        static void NextUpdate(Action action)
        {
            EditorApplication.CallbackFunction once = null;
            once = () =>
            {
                EditorApplication.update -= once;
                action();
            };
            EditorApplication.update += once;
        }

        [McpTool("read_console",
            "Read Unity console messages captured by Direct MCP (survives script reloads). Errors include stack traces.",
            InputSchema = @"{""type"":""object"",""properties"":{
                ""types"":{""type"":""array"",""items"":{""type"":""string"",""enum"":[""error"",""warning"",""log""]},""description"":""Default: all types.""},
                ""limit"":{""type"":""integer"",""description"":""Most recent N messages. Default 50.""},
                ""since_id"":{""type"":""integer"",""description"":""Only messages with id greater than this.""},
                ""clear"":{""type"":""boolean"",""description"":""Clear the buffer after reading.""}}}",
            MainThreadOnly = false)]
        public static ToolResult ReadConsole(JObject args)
        {
            var types = args["types"] as JArray;
            int limit = (int?)args["limit"] ?? 50;
            long since = (long?)args["since_id"] ?? 0;

            var list = LogBuffer.Query(e => e.Id > since && (types == null || types.Count == 0 || types.Values<string>().Contains(e.Type)));
            if (list.Count > limit) list = list.GetRange(list.Count - limit, limit);
            if ((bool?)args["clear"] == true) LogBuffer.Clear();

            if (list.Count == 0) return ToolResult.Text("No console messages.");
            var sb = new System.Text.StringBuilder();
            foreach (var e in list)
            {
                sb.Append('#').Append(e.Id).Append(' ').Append(e.Time).Append(' ').Append(e.Type.ToUpperInvariant()).Append(": ").AppendLine(e.Message);
                if (!string.IsNullOrEmpty(e.Stack)) sb.AppendLine("    " + e.Stack.Trim().Replace("\n", "\n    "));
            }
            return ToolResult.Text(sb.ToString());
        }
    }
}
