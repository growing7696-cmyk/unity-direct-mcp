// The snippet-wrapping and result-serialization approach is adapted from MCP for Unity's
// ExecuteCode tool (https://github.com/CoplayDev/unity-mcp, MIT License, Copyright (c) 2025 CoplayDev).
// Compilation here uses the Roslyn compiler that ships inside the Unity Editor, so no extra packages are needed.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace DirectMcp.Tools
{
    public static class ExecuteCsharp
    {
        const string ClassName = "DirectMcpSnippet";

        static readonly string[] DefaultUsings =
        {
            "System", "System.Collections", "System.Collections.Generic", "System.IO", "System.Linq", "System.Reflection", "System.Text",
            "UnityEngine", "UnityEngine.SceneManagement", "UnityEditor", "UnityEditor.SceneManagement",
        };

        [McpTool("execute_csharp",
            "Compile and run C# in the Unity Editor (full UnityEngine + UnityEditor API, plus your project's scripts). " +
            "'code' is the body of a static method returning object: end with 'return <value>;' to get a result back. " +
            "Debug.Log output is returned too. Use Undo.RegisterCreatedObjectUndo / Undo.AddComponent / Undo.RecordObject " +
            "so the user can undo your changes. Find objects by path with DirectMcp.Tools.ObjectPaths.Find(\"Parent/Child\"). " +
            "Save with EditorSceneManager.SaveOpenScenes() and AssetDatabase.SaveAssets().",
            InputSchema = @"{""type"":""object"",""required"":[""code""],""properties"":{
                ""code"":{""type"":""string"",""description"":""Method body. Default usings: System, System.Linq, System.Collections.Generic, System.IO, UnityEngine, UnityEditor, UnityEngine.SceneManagement, UnityEditor.SceneManagement.""},
                ""usings"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Extra namespaces, e.g. [\""UnityEngine.UI\""].""},
                ""read_only"":{""type"":""boolean"",""description"":""Set true for queries. Otherwise open scenes are marked modified so changes are saved.""}}}",
            MainThreadOnly = false)]
        public static ToolResult Run(JObject args)
        {
            var code = (string)args["code"];
            if (string.IsNullOrWhiteSpace(code)) return ToolResult.Error("'code' is empty.");
            var usings = DefaultUsings.Concat((args["usings"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>()).Distinct();
            bool readOnly = (bool?)args["read_only"] ?? false;

            var source = new StringBuilder();
            foreach (var u in usings) source.Append("using ").Append(u).AppendLine(";");
            source.AppendLine("using Object = UnityEngine.Object;");
            source.AppendLine("using Debug = UnityEngine.Debug;");
            source.AppendLine($"public static class {ClassName} {{ public static object Run() {{");
            source.AppendLine("#line 1 \"snippet\"");
            source.AppendLine(code);
            source.AppendLine("#line hidden");
            source.AppendLine("return null; } }");

            string[] references;
            try { references = MainThread.Invoke(ReferencePaths); }
            catch (ReloadingException e) { return ToolResult.Text(e.Message); }

            var compileSw = Stopwatch.StartNew();
            var dll = Compile(source.ToString(), references, out var errors);
            if (dll == null)
                return ToolResult.Error("Compile failed (line numbers are relative to your code):\n" + string.Join("\n", errors));
            compileSw.Stop();

            try
            {
                return MainThread.Invoke(() => Execute(dll, readOnly, compileSw.ElapsedMilliseconds));
            }
            catch (ReloadingException e) { return ToolResult.Text(e.Message); }
        }

        static ToolResult Execute(byte[] dll, bool readOnly, long compileMs)
        {
            var firstLog = LogBuffer.LastId;
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Direct MCP: execute_csharp");

            // Marked before running so a snippet that saves the scene ends up clean.
            if (!readOnly && !EditorApplication.isPlaying)
                EditorSceneManager.MarkAllScenesDirty();

            object value = null;
            Exception error = null;
            var sw = Stopwatch.StartNew();
            try
            {
                var asm = Assembly.Load(dll);
                value = asm.GetType(ClassName).GetMethod("Run").Invoke(null, null);
            }
            catch (TargetInvocationException e) { error = e.InnerException ?? e; }
            catch (Exception e) { error = e; }
            sw.Stop();
            Undo.CollapseUndoOperations(undoGroup);

            var sb = new StringBuilder();
            if (error != null)
            {
                sb.Append("Runtime exception: ").Append(error.GetType().Name).Append(": ").AppendLine(error.Message);
                var stack = error.StackTrace ?? "";
                int cut = stack.IndexOf("(wrapper managed-to-native)", StringComparison.Ordinal);
                if (cut < 0) cut = stack.IndexOf("at System.Reflection.", StringComparison.Ordinal);
                sb.AppendLine((cut > 0 ? stack.Substring(0, cut) : stack).TrimEnd());
            }
            else
            {
                sb.Append("Result: ").AppendLine(Describe(value));
            }

            var logs = LogBuffer.Query(e => e.Id > firstLog);
            if (logs.Count > 0)
            {
                sb.AppendLine().AppendLine("Console output:");
                foreach (var l in logs.Take(100)) sb.Append("  ").Append(l.Type).Append(": ").AppendLine(l.Message);
            }
            sb.AppendLine().Append($"(compile {compileMs} ms, run {sw.ElapsedMilliseconds} ms)");

            var result = ToolResult.Text(sb.ToString());
            result.IsError = error != null;
            return result;
        }

        // ─── Compilation with the Editor's bundled Roslyn ───

        static string[] ReferencePaths()
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.IsDynamic) continue;
                string loc;
                try { loc = a.Location; } catch { continue; }
                if (string.IsNullOrEmpty(loc) || !File.Exists(loc)) continue;
                var name = a.GetName().Name;
                if (!byName.ContainsKey(name)) byName[name] = loc;
            }
            return byName.Values.ToArray();
        }

        static (string dotnet, string csc) _compiler;

        static (string dotnet, string csc) FindCompiler()
        {
            if (_compiler.csc != null) return _compiler;
            string exe = Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet";
            var contents = EditorApplication.applicationContentsPath;
            var roots = new[]
            {
                Path.Combine(contents, "Resources", "Scripting"),
                Path.Combine(contents, "Scripting"),
                contents,
            };
            foreach (var root in roots)
            {
                var sdk = Path.Combine(root, "DotNetSdk");
                var dotnet = Path.Combine(sdk, exe);
                var sdkDir = Path.Combine(sdk, "sdk");
                if (File.Exists(dotnet) && Directory.Exists(sdkDir))
                {
                    var csc = Directory.GetDirectories(sdkDir)
                        .OrderByDescending(d => d, StringComparer.Ordinal)
                        .Select(d => Path.Combine(d, "Roslyn", "bincore", "csc.dll"))
                        .FirstOrDefault(File.Exists);
                    if (csc != null) return _compiler = (dotnet, csc);
                }
                // Older layout (Unity 2021-2023).
                var legacyCsc = Path.Combine(root, "DotNetSdkRoslyn", "csc.dll");
                var legacyDotnet = Path.Combine(root, "NetCoreRuntime", exe);
                if (File.Exists(legacyCsc) && File.Exists(legacyDotnet)) return _compiler = (legacyDotnet, legacyCsc);
            }
            return (null, null);
        }

        static byte[] Compile(string source, string[] references, out List<string> errors)
        {
            errors = new List<string>();
            var (dotnet, csc) = FindCompiler();
            if (csc == null)
            {
                errors.Add("Could not find the Roslyn compiler inside the Unity Editor installation (" + EditorApplication.applicationContentsPath + ").");
                return null;
            }

            var dir = Path.Combine(Path.GetTempPath(), "DirectMcp");
            Directory.CreateDirectory(dir);
            var id = Guid.NewGuid().ToString("N");
            var src = Path.Combine(dir, id + ".cs");
            var outDll = Path.Combine(dir, id + ".dll");
            var rsp = Path.Combine(dir, id + ".rsp");
            try
            {
                File.WriteAllText(src, source);
                var r = new StringBuilder();
                r.AppendLine("/target:library");
                r.AppendLine("/nostdlib+");
                r.AppendLine("/langversion:9.0");
                r.AppendLine("/nologo");
                r.AppendLine("/preferreduilang:en-US");
                r.AppendLine("/nowarn:1701,1702,0162,0618,0649");
                r.AppendLine($"/out:\"{outDll}\"");
                foreach (var reference in references) r.AppendLine($"/r:\"{reference}\"");
                r.AppendLine($"\"{src}\"");
                File.WriteAllText(rsp, r.ToString());

                var psi = new ProcessStartInfo(dotnet, $"\"{csc}\" /noconfig @\"{rsp}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.EnvironmentVariables["DOTNET_CLI_UI_LANGUAGE"] = "en";
                psi.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
                psi.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
                using (var p = Process.Start(psi))
                {
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(60000))
                    {
                        try { p.Kill(); } catch { }
                        errors.Add("Compiler timed out.");
                        return null;
                    }
                    var output = stdout.Result + "\n" + stderr.Result;
                    if (p.ExitCode != 0 || !File.Exists(outDll))
                    {
                        foreach (var line in output.Split('\n'))
                            if (line.Contains("error"))
                            {
                                // Errors in the user's code read "snippet(l,c)"; errors in the generated using lines
                                // (an unknown namespace in 'usings') would otherwise show the temp file path.
                                var clean = Regex.Replace(line.Trim(), @"^.*snippet\(", "line(");
                                errors.Add(Regex.Replace(clean, @"^.*[0-9a-f]{32}\.cs\(\d+,\d+\)", "usings"));
                            }
                        if (errors.Count == 0) errors.Add(output.Trim());
                        return null;
                    }
                }
                return File.ReadAllBytes(outDll);
            }
            finally
            {
                foreach (var f in new[] { src, outDll, rsp })
                    try { File.Delete(f); } catch { }
            }
        }

        // ─── Result formatting ───

        static string Describe(object value, int depth = 0)
        {
            switch (value)
            {
                case null: return "null";
                case string s: return s;
                case Object o when o == null: return "null (destroyed object)";
                case GameObject go: return go.scene.IsValid() ? $"GameObject '{ObjectPaths.PathOf(go)}'" : $"GameObject '{go.name}' ({AssetDatabase.GetAssetPath(go)})";
                case Component c: return $"{c.GetType().Name} on '{ObjectPaths.PathOf(c.gameObject)}'";
                case Object o:
                    var path = AssetDatabase.GetAssetPath(o);
                    return $"{o.GetType().Name} '{o.name}'" + (string.IsNullOrEmpty(path) ? "" : $" ({path})");
                case IDictionary dict when depth < 2:
                    return "{" + string.Join(", ", dict.Keys.Cast<object>().Take(100).Select(k => $"{Describe(k, depth + 1)}: {Describe(dict[k], depth + 1)}")) + "}";
                case IEnumerable list when depth < 2:
                    var items = list.Cast<object>().Take(201).Select(i => Describe(i, depth + 1)).ToList();
                    var more = items.Count > 200 ? ", ..." : "";
                    return "[" + string.Join(", ", items.Take(200)) + more + "]";
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is decimal || type.Namespace?.StartsWith("Unity") == true)
                return value.ToString();
            try
            {
                var json = JsonConvert.SerializeObject(value, Formatting.Indented, new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    Converters = { new UnityToStringConverter() },
                    Error = (_, e) => e.ErrorContext.Handled = true,
                });
                return json.Length > 20000 ? json.Substring(0, 20000) + "\n... (truncated)" : json;
            }
            catch
            {
                return value.ToString();
            }
        }

        /// <summary>Unity structs like Vector3 recurse forever in Json.NET (normalized.normalized...), so print them instead.</summary>
        sealed class UnityToStringConverter : JsonConverter
        {
            public override bool CanConvert(Type t) => t.Namespace != null && t.Namespace.StartsWith("Unity");
            public override bool CanRead => false;
            public override object ReadJson(JsonReader r, Type t, object v, JsonSerializer s) => throw new NotSupportedException();
            public override void WriteJson(JsonWriter w, object value, JsonSerializer s) => w.WriteValue(Describe(value, 2));
        }
    }
}
