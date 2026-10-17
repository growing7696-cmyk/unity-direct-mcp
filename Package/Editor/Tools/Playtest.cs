using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DirectMcp.Tools
{
    public static class Playtest
    {
        const string ClassName = "DirectMcpWatch";

        static readonly string[] DefaultUsings =
        {
            "System", "System.Collections.Generic", "System.Linq", "UnityEngine", "UnityEngine.SceneManagement", "UnityEditor",
        };

        [McpTool("playtest",
            "Let the running game play for a while and watch it, in one call. Requires play mode (play_mode enter first). " +
            "Samples C# expressions over game time (e.g. the player's position, enemies alive, the score), takes screenshots " +
            "along the way, and reports console errors and warnings raised during the run. Use it to check that things " +
            "actually happen: enemies move, health drops, the score changes, nothing throws.",
            InputSchema = @"{""type"":""object"",""properties"":{
                ""seconds"":{""type"":""number"",""description"":""Game time to run. Default 10, max 120.""},
                ""watch"":{""type"":""array"",""items"":{""type"":""string""},""description"":""C# expressions sampled during the run, e.g. \""GameObject.Find(\\\""Player\\\"").transform.position\"", \""GameObject.FindGameObjectsWithTag(\\\""Enemy\\\"").Length\"". Your project's classes are available.""},
                ""samples"":{""type"":""integer"",""description"":""How many times to sample the watch expressions, evenly spread from start to end. Default 6, max 30.""},
                ""screenshots"":{""type"":""integer"",""description"":""Screenshots evenly spread over the run, the last one at the end. Default 2, max 6. 0 for none.""},
                ""time_scale"":{""type"":""number"",""description"":""Run faster or slower (Time.timeScale), restored afterwards. Default: unchanged.""},
                ""usings"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Extra namespaces for the watch expressions.""}}}",
            MainThreadOnly = false)]
        public static ToolResult Run(JObject args)
        {
            double seconds = Math.Min(120, Math.Max(0.5, (double?)args["seconds"] ?? 10));
            int samples = Math.Min(30, Math.Max(2, (int?)args["samples"] ?? 6));
            int shots = Math.Min(6, Math.Max(0, (int?)args["screenshots"] ?? 2));
            float? timeScale = (float?)args["time_scale"];
            var watch = (args["watch"] as JArray)?.Values<string>().Where(e => !string.IsNullOrWhiteSpace(e)).ToList() ?? new List<string>();
            var usings = DefaultUsings.Concat((args["usings"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>()).Distinct();

            try
            {
                var state = MainThread.Invoke(() => EditorApplication.isPlaying ? (EditorApplication.isPaused ? "paused" : "playing") : "editing");
                if (state == "editing") return ToolResult.Error("Not in play mode. Call play_mode enter, then unity_status with wait_seconds=30, then playtest.");
                if (state == "paused") return ToolResult.Error("Play mode is paused. Call play_mode resume first.");
            }
            catch (ReloadingException e) { return ToolResult.Text(e.Message); }

            Func<object[]> evaluate = null;
            if (watch.Count > 0)
            {
                var error = CompileWatch(watch, usings, out evaluate);
                if (error != null) return ToolResult.Error(error);
            }

            var result = new ToolResult();
            var rows = new List<(double t, string[] values)>();
            var shotTimes = Enumerable.Range(1, shots).Select(i => seconds * i / shots).ToList();
            var images = new List<(double t, Capture.Shot shot)>();
            string stopped = null;
            long firstLog = LogBuffer.LastId;
            float previousScale = 1;
            double startTime = 0;
            int startFrame = 0, endFrame = 0;
            double endTime = 0;
            var real = Stopwatch.StartNew();

            try
            {
                MainThread.Invoke(() =>
                {
                    previousScale = Time.timeScale;
                    if (timeScale.HasValue) Time.timeScale = Mathf.Clamp(timeScale.Value, 0.01f, 100f);
                    startTime = Time.time;
                    startFrame = Time.frameCount;
                    return 0;
                });
                float speed = timeScale.HasValue ? Mathf.Clamp(timeScale.Value, 0.01f, 100f) : previousScale;
                // Generous real-time budget: a throttled background Editor runs at about 10 fps.
                double budget = seconds / Math.Max(0.01, speed) * 3 + 15;

                int nextSample = 0, nextShot = 0;
                double lastProgressAt = 0;
                int lastFrame = startFrame;
                while (true)
                {
                    var (t, frame, playing) = MainThread.Invoke(() => (Time.time - startTime, Time.frameCount, EditorApplication.isPlaying));
                    endTime = t;
                    endFrame = frame;
                    if (!playing) { stopped = $"Play mode ended at t={t:0.0} s."; break; }

                    while (nextSample < samples && t >= seconds * nextSample / (samples - 1))
                    {
                        var values = evaluate == null ? Array.Empty<string>() : MainThread.Invoke(() => evaluate().Select(Format).ToArray());
                        rows.Add((t, values));
                        nextSample++;
                    }
                    while (nextShot < shotTimes.Count && t >= shotTimes[nextShot])
                    {
                        images.Add((t, MainThread.Invoke(() => Capture.Take("game", null, 640, 360))));
                        nextShot++;
                    }
                    if (nextSample >= samples && nextShot >= shotTimes.Count) break;

                    if (frame != lastFrame) { lastFrame = frame; lastProgressAt = real.Elapsed.TotalSeconds; }
                    else if (real.Elapsed.TotalSeconds - lastProgressAt > 5)
                    {
                        stopped = $"The game stopped advancing at frame {frame} (paused, a breakpoint, or a modal dialog?).";
                        break;
                    }
                    if (real.Elapsed.TotalSeconds > budget) { stopped = $"Gave up after {real.Elapsed.TotalSeconds:0} s real time."; break; }
                    Thread.Sleep(40);
                }
            }
            catch (ReloadingException)
            {
                stopped = "Unity started reloading scripts (play mode ended or scripts recompiled).";
            }
            finally
            {
                if (timeScale.HasValue)
                {
                    try { MainThread.Invoke(() => { if (EditorApplication.isPlaying) Time.timeScale = previousScale; return 0; }); }
                    catch (ReloadingException) { }
                }
            }

            var sb = new StringBuilder();
            int frames = endFrame - startFrame;
            double realSeconds = real.Elapsed.TotalSeconds;
            sb.Append($"Played {endTime:0.0} s of game time in {realSeconds:0.0} s real time, {frames} frames");
            if (realSeconds > 0.5) sb.Append($" (~{frames / realSeconds:0} fps").Append(frames / realSeconds < 20 ? ", the Editor throttles when it is in the background" : "").Append(')');
            sb.AppendLine(".");
            if (stopped != null) sb.AppendLine("Stopped early: " + stopped);

            if (watch.Count > 0 && rows.Count > 0)
            {
                sb.AppendLine().Append("| t (s) |");
                for (int i = 0; i < watch.Count; i++) sb.Append(' ').Append(Cell(watch[i], 60)).Append(" |");
                sb.AppendLine().Append("|---|").Append(string.Concat(Enumerable.Repeat("---|", watch.Count))).AppendLine();
                foreach (var (t, values) in rows)
                {
                    sb.Append("| ").Append(t.ToString("0.0")).Append(" |");
                    foreach (var v in values) sb.Append(' ').Append(Cell(v, 120)).Append(" |");
                    sb.AppendLine();
                }
            }

            AppendConsole(sb, firstLog);
            result.AddText(sb.ToString().TrimEnd());
            foreach (var (t, shot) in images)
            {
                if (shot.Png == null) result.AddText($"Screenshot at t={t:0.0} s failed: {shot.Error}");
                else result.AddText($"Screenshot at t={t:0.0} s").AddImage(shot.Png);
            }
            return result;
        }

        static string CompileWatch(List<string> expressions, IEnumerable<string> usings, out Func<object[]> evaluate)
        {
            evaluate = null;
            var source = new StringBuilder();
            foreach (var u in usings) source.Append("using ").Append(u).AppendLine(";");
            source.AppendLine("using Object = UnityEngine.Object;");
            source.AppendLine($"public static class {ClassName} {{");
            source.AppendLine("static object E(System.Func<object> f) { try { return f(); } catch (System.Exception e) { return \"<\" + e.GetType().Name + \": \" + e.Message + \">\"; } }");
            source.AppendLine("public static object[] Run() { return new object[] {");
            for (int i = 0; i < expressions.Count; i++)
            {
                // Compile errors then read "line(N,col)" with N = the watch expression's number.
                source.AppendLine($"#line {i + 1} \"snippet\"");
                source.Append("E(() => (object)(").Append(expressions[i]).AppendLine(")),");
            }
            source.AppendLine("#line hidden");
            source.AppendLine("}; } }");

            string[] references;
            try { references = MainThread.Invoke(ExecuteCsharp.ReferencePaths); }
            catch (ReloadingException e) { return e.Message; }
            var dll = ExecuteCsharp.Compile(source.ToString(), references, out var errors);
            if (dll == null) return "A watch expression did not compile (line N = expression N):\n" + string.Join("\n", errors);

            var run = Assembly.Load(dll).GetType(ClassName).GetMethod("Run");
            evaluate = () => (object[])run.Invoke(null, null);
            return null;
        }

        static string Format(object value)
        {
            var s = ExecuteCsharp.Describe(value);
            return s.Length > 300 ? s.Substring(0, 300) + "..." : s;
        }

        static string Cell(string s, int max)
        {
            s = s.Replace("\r", "").Replace("\n", " ").Replace("|", "\\|");
            return s.Length > max ? s.Substring(0, max) + "..." : s;
        }

        static void AppendConsole(StringBuilder sb, long firstLog)
        {
            var logs = LogBuffer.Query(e => e.Id > firstLog);
            int errors = logs.Count(e => e.Type == "error"), warnings = logs.Count(e => e.Type == "warning");
            sb.AppendLine().AppendLine($"Console during the run: {errors} errors, {warnings} warnings, {logs.Count - errors - warnings} logs.");

            // Repeated messages (an exception every frame) are grouped so one problem doesn't hide the others.
            var problems = logs.Where(e => e.Type != "log")
                .GroupBy(e => e.Type + ": " + FirstLine(e.Message))
                .OrderBy(g => g.First().Type == "error" ? 0 : 1)
                .Take(15);
            foreach (var g in problems)
            {
                sb.Append("  ").Append(g.Key);
                if (g.Count() > 1) sb.Append($" (x{g.Count()})");
                sb.AppendLine();
                var stack = g.First().Stack;
                if (!string.IsNullOrEmpty(stack)) sb.Append("    ").AppendLine(FirstLine(stack.Trim()));
            }
            var recent = logs.Where(e => e.Type == "log").TakeLast(10).ToList();
            if (recent.Count > 0)
            {
                sb.AppendLine("  Last logs:");
                foreach (var e in recent) sb.Append("    ").AppendLine(Cell(e.Message, 200));
            }
            if (errors + warnings > 0) sb.AppendLine("  Full messages and stack traces: read_console.");
        }

        static string FirstLine(string s)
        {
            int nl = s.IndexOf('\n');
            return nl < 0 ? s : s.Substring(0, nl).TrimEnd();
        }
    }
}
