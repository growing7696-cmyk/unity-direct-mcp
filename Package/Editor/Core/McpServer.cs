using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DirectMcp
{
    /// <summary>
    /// A Model Context Protocol server (Streamable HTTP transport, JSON responses) hosted
    /// directly inside the Unity Editor. Listens on 127.0.0.1 only.
    /// </summary>
    [InitializeOnLoad]
    public static class McpServer
    {
        public const string Version = "0.3.0";
        static readonly string[] SupportedProtocols = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

        const string Instructions =
@"You are connected to a live Unity Editor.
Workflow that works well:
1. Call unity_status and scene_hierarchy first to see what exists. Never guess object names.
2. Write or edit C# scripts with your own file tools (files under Assets/), then call refresh_assets and unity_status(wait_seconds=60) to get compile errors. Fix errors before continuing.
3. Use execute_csharp for anything in the Editor API: creating GameObjects, adding components, setting fields, making prefabs/materials, saving scenes (EditorSceneManager.SaveOpenScenes()).
4. Verify your work: screenshot to look at the result (in play mode it includes the UI), play_mode(enter), then playtest to let the game run while watching values and collecting errors, then play_mode(exit).
Unity reloads scripts after a successful compile and when entering play mode. During that window (2-10 s) calls may fail or report that Unity is reloading: wait a few seconds and call unity_status again.";

        static HttpListener _listener;
        static Thread _thread;
        static int _inflight;

        public static int Port { get; private set; }
        public static bool IsRunning => _listener != null && _listener.IsListening;
        public static string Url => $"http://127.0.0.1:{Port}/mcp";
        public static string LastError { get; private set; }

        static McpServer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            EditorApplication.quitting += Stop;
            if (Settings.Enabled) Start();
        }

        public static void Start()
        {
            if (IsRunning) return;
            LastError = null;
            int preferred = Settings.Port;
            for (int p = preferred; p < preferred + 10; p++)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{p}/");
                try
                {
                    listener.Start();
                }
                catch (Exception e)
                {
                    LastError = $"Port {p}: {e.Message}";
                    try { listener.Close(); } catch { }
                    continue;
                }
                _listener = listener;
                Port = p;
                _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "DirectMcp.Http" };
                _thread.Start();
                LastError = null;
                if (!SessionState.GetBool("DirectMcp.Announced", false))
                {
                    SessionState.SetBool("DirectMcp.Announced", true);
                    UnityEngine.Debug.Log($"[Direct MCP] Listening on {Url}");
                }
                return;
            }
            UnityEngine.Debug.LogWarning($"[Direct MCP] Could not start server: {LastError}");
        }

        public static void Stop()
        {
            var l = _listener;
            _listener = null;
            if (l == null) return;
            try { l.Stop(); l.Close(); } catch { }
        }

        public static void Restart()
        {
            Stop();
            Start();
        }

        static void OnBeforeReload()
        {
            // Answer every request that is waiting on the main thread, give the answers a moment
            // to reach the client, then release the port for the next domain.
            MainThread.CancelPending();
            var sw = Stopwatch.StartNew();
            while (Volatile.Read(ref _inflight) > 0 && sw.ElapsedMilliseconds < 1500)
                Thread.Sleep(10);
            Stop();
        }

        static void AcceptLoop()
        {
            var listener = _listener;
            while (listener != null && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }
                Interlocked.Increment(ref _inflight);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Handle(ctx); }
                    catch (Exception e) { TryWrite(ctx, 500, "text/plain", e.Message); }
                    finally { Interlocked.Decrement(ref _inflight); }
                });
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var origin = req.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) && !IsLocalOrigin(origin))
            {
                TryWrite(ctx, 403, "text/plain", "Forbidden origin");
                return;
            }

            var path = req.Url.AbsolutePath.TrimEnd('/');
            if (path != "/mcp" && path != "")
            {
                TryWrite(ctx, 404, "text/plain", "Not found. MCP endpoint is /mcp");
                return;
            }

            if (req.HttpMethod == "GET" || req.HttpMethod == "DELETE")
            {
                // No server-initiated stream and no sessions to end.
                ctx.Response.AddHeader("Allow", "POST");
                TryWrite(ctx, 405, "text/plain", "Use POST");
                return;
            }
            if (req.HttpMethod != "POST")
            {
                TryWrite(ctx, 405, "text/plain", "Use POST");
                return;
            }

            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                body = reader.ReadToEnd();

            JToken incoming;
            try { incoming = JToken.Parse(body); }
            catch
            {
                WriteJson(ctx, ErrorResponse(null, -32700, "Parse error"));
                return;
            }

            if (incoming is JArray batch)
            {
                var responses = new JArray();
                foreach (var item in batch)
                    if (item is JObject o && HandleMessage(o) is JObject r) responses.Add(r);
                if (responses.Count == 0) TryWrite(ctx, 202, null, null);
                else WriteJson(ctx, responses);
                return;
            }

            var response = incoming is JObject msg ? HandleMessage(msg) : ErrorResponse(null, -32600, "Invalid Request");
            if (response == null) TryWrite(ctx, 202, null, null);
            else WriteJson(ctx, response);
        }

        static JObject HandleMessage(JObject msg)
        {
            var method = (string)msg["method"];
            var id = msg["id"];
            if (method == null) return null;            // a response from the client: nothing to do
            bool isNotification = id == null || id.Type == JTokenType.Null;
            if (isNotification) return null;

            try
            {
                switch (method)
                {
                    case "initialize":
                        var requested = (string)msg["params"]?["protocolVersion"];
                        var version = Array.IndexOf(SupportedProtocols, requested) >= 0 ? requested : SupportedProtocols[0];
                        return Result(id, new JObject
                        {
                            ["protocolVersion"] = version,
                            ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                            ["serverInfo"] = new JObject { ["name"] = "unity-direct-mcp", ["version"] = Version },
                            ["instructions"] = Instructions,
                        });
                    case "ping":
                        return Result(id, new JObject());
                    case "tools/list":
                        return Result(id, new JObject { ["tools"] = ToolRegistry.ListJson() });
                    case "tools/call":
                        var name = (string)msg["params"]?["name"];
                        var args = msg["params"]?["arguments"] as JObject;
                        var sw = Stopwatch.StartNew();
                        var result = ToolRegistry.Call(name, args);
                        ActivityLog.Add(name, !result.IsError, sw.ElapsedMilliseconds);
                        return Result(id, result.ToJson());
                    default:
                        return ErrorResponse(id, -32601, $"Method not found: {method}");
                }
            }
            catch (Exception e)
            {
                return ErrorResponse(id, -32603, e.Message);
            }
        }

        static bool IsLocalOrigin(string origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
            return uri.Host == "localhost" || uri.Host == "127.0.0.1" || uri.Host == "[::1]" || uri.Host == "::1";
        }

        static JObject Result(JToken id, JToken result) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

        static JObject ErrorResponse(JToken id, int code, string message) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = code, ["message"] = message } };

        static void WriteJson(HttpListenerContext ctx, JToken json) =>
            TryWrite(ctx, 200, "application/json", json.ToString(Formatting.None));

        static void TryWrite(HttpListenerContext ctx, int status, string contentType, string body)
        {
            try
            {
                var res = ctx.Response;
                res.StatusCode = status;
                if (contentType != null) res.ContentType = contentType + "; charset=utf-8";
                var bytes = body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
                res.ContentLength64 = bytes.Length;
                if (bytes.Length > 0) res.OutputStream.Write(bytes, 0, bytes.Length);
                res.OutputStream.Close();
            }
            catch
            {
                // Client went away.
            }
        }
    }

    public static class ActivityLog
    {
        public struct Entry
        {
            public DateTime Time;
            public string Tool;
            public bool Ok;
            public long Ms;
        }

        static readonly List<Entry> Entries = new List<Entry>();
        public static int TotalCalls { get; private set; }

        public static void Add(string tool, bool ok, long ms)
        {
            lock (Entries)
            {
                Entries.Add(new Entry { Time = DateTime.Now, Tool = tool, Ok = ok, Ms = ms });
                if (Entries.Count > 30) Entries.RemoveAt(0);
                TotalCalls++;
            }
        }

        public static Entry[] Snapshot()
        {
            lock (Entries) return Entries.ToArray();
        }
    }

    public static class Settings
    {
        static string Key(string name) => $"DirectMcp.{name}.{StableHash(Application.dataPath)}";

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(Key("Enabled"), true);
            set => EditorPrefs.SetBool(Key("Enabled"), value);
        }

        /// <summary>Default port is derived from the project path so several projects can run side by side.</summary>
        public static int Port
        {
            get => EditorPrefs.GetInt(Key("Port"), 47000 + (int)(StableHash(Application.dataPath) % 1000));
            set => EditorPrefs.SetInt(Key("Port"), value);
        }

        static uint StableHash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h;
        }
    }
}
