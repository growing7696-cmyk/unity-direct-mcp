using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace DirectMcp
{
    /// <summary>Console messages, kept across domain reloads so the agent can still read them afterwards.</summary>
    [InitializeOnLoad]
    public static class LogBuffer
    {
        public sealed class Entry
        {
            public long Id;
            public string Type;      // error | warning | log
            public string Message;
            public string Stack;
            public string Time;
        }

        const int Capacity = 1000;
        const string SessionKey = "DirectMcp.Logs";
        static readonly List<Entry> Entries = new List<Entry>();
        static long _nextId = 1;

        static LogBuffer()
        {
            var saved = SessionState.GetString(SessionKey, null);
            if (!string.IsNullOrEmpty(saved))
            {
                try
                {
                    Entries.AddRange(JsonConvert.DeserializeObject<List<Entry>>(saved));
                    if (Entries.Count > 0) _nextId = Entries[Entries.Count - 1].Id + 1;
                }
                catch { /* ignore a corrupt snapshot */ }
            }
            Application.logMessageReceivedThreaded += OnLog;
            AssemblyReloadEvents.beforeAssemblyReload += Save;
        }

        public static long LastId
        {
            get { lock (Entries) return _nextId - 1; }
        }

        static void OnLog(string message, string stack, LogType type)
        {
            if (message != null && message.StartsWith("[Direct MCP]")) return;
            var kind = type == LogType.Warning ? "warning" : type == LogType.Log ? "log" : "error";
            lock (Entries)
            {
                Entries.Add(new Entry
                {
                    Id = _nextId++,
                    Type = kind,
                    Message = message,
                    Stack = kind == "error" ? Trim(stack, 1500) : null,
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                });
                if (Entries.Count > Capacity) Entries.RemoveRange(0, Entries.Count - Capacity);
            }
        }

        public static List<Entry> Query(Func<Entry, bool> filter)
        {
            lock (Entries) return Entries.Where(filter).ToList();
        }

        public static void Clear()
        {
            lock (Entries) Entries.Clear();
        }

        public static (int errors, int warnings) Counts()
        {
            lock (Entries) return (Entries.Count(e => e.Type == "error"), Entries.Count(e => e.Type == "warning"));
        }

        static void Save()
        {
            lock (Entries)
                SessionState.SetString(SessionKey, JsonConvert.SerializeObject(Entries.Skip(Math.Max(0, Entries.Count - 300)).ToList()));
        }

        static string Trim(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    /// <summary>Result of the last script compilation, kept across domain reloads.</summary>
    [InitializeOnLoad]
    public static class CompileState
    {
        public sealed class Snapshot
        {
            public string FinishedAt;
            public bool Succeeded = true;
            public List<string> Errors = new List<string>();
            public int WarningCount;
            public long Sequence;
        }

        const string SessionKey = "DirectMcp.Compile";
        static Snapshot _current = new Snapshot();
        static Snapshot _building;

        public static Snapshot Last => _current;

        static CompileState()
        {
            var saved = SessionState.GetString(SessionKey, null);
            if (!string.IsNullOrEmpty(saved))
            {
                try { _current = JsonConvert.DeserializeObject<Snapshot>(saved); } catch { }
            }
            CompilationPipeline.compilationStarted += _ => _building = new Snapshot { Sequence = _current.Sequence + 1 };
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyFinished;
            CompilationPipeline.compilationFinished += _ => Finish();
        }

        static void OnAssemblyFinished(string assembly, CompilerMessage[] messages)
        {
            _building ??= new Snapshot { Sequence = _current.Sequence + 1 };
            foreach (var m in messages)
            {
                if (m.type == CompilerMessageType.Error)
                {
                    if (_building.Errors.Count < 50) _building.Errors.Add(m.message);
                }
                else if (m.type == CompilerMessageType.Warning)
                {
                    _building.WarningCount++;
                }
            }
        }

        static void Finish()
        {
            var snap = _building ?? new Snapshot { Sequence = _current.Sequence + 1 };
            snap.Succeeded = snap.Errors.Count == 0;
            snap.FinishedAt = DateTime.Now.ToString("HH:mm:ss");
            _current = snap;
            _building = null;
            SessionState.SetString(SessionKey, JsonConvert.SerializeObject(snap));
        }
    }
}
