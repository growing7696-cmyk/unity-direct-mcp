using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace DirectMcp
{
    /// <summary>
    /// Marks a static method <c>ToolResult Name(JObject args)</c> as an MCP tool.
    /// Add your own tools anywhere in an Editor assembly; they are picked up automatically.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class McpToolAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }
        /// <summary>JSON Schema of the arguments object. Defaults to an empty object.</summary>
        public string InputSchema { get; set; } = "{\"type\":\"object\",\"properties\":{}}";
        /// <summary>
        /// By default tools run on Unity's main thread. Set to false for tools that wait or do slow
        /// work themselves; they must then use <see cref="MainThread.Invoke{T}"/> for Unity API calls.
        /// </summary>
        public bool MainThreadOnly { get; set; } = true;

        public McpToolAttribute(string name, string description)
        {
            Name = name;
            Description = description;
        }
    }

    public sealed class ToolResult
    {
        public readonly JArray Content = new JArray();
        public bool IsError;

        public static ToolResult Text(string text) => new ToolResult().AddText(text);
        public static ToolResult Error(string text) { var r = Text(text); r.IsError = true; return r; }

        public ToolResult AddText(string text)
        {
            Content.Add(new JObject { ["type"] = "text", ["text"] = text ?? "" });
            return this;
        }

        public ToolResult AddImage(byte[] png)
        {
            Content.Add(new JObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" });
            return this;
        }

        public JObject ToJson() => new JObject { ["content"] = Content, ["isError"] = IsError };
    }

    public static class ToolRegistry
    {
        public sealed class Tool
        {
            public McpToolAttribute Info;
            public MethodInfo Method;
            public JObject Schema;
        }

        static Dictionary<string, Tool> _tools;

        public static IReadOnlyDictionary<string, Tool> Tools
        {
            get
            {
                if (_tools == null) Discover();
                return _tools;
            }
        }

        static void Discover()
        {
            var found = new Dictionary<string, Tool>();
            foreach (var m in TypeCache.GetMethodsWithAttribute<McpToolAttribute>())
            {
                if (!m.IsStatic || m.ReturnType != typeof(ToolResult)) continue;
                var p = m.GetParameters();
                if (p.Length != 1 || p[0].ParameterType != typeof(JObject)) continue;
                var info = m.GetCustomAttribute<McpToolAttribute>();
                found[info.Name] = new Tool { Info = info, Method = m, Schema = JObject.Parse(info.InputSchema) };
            }
            _tools = found;
        }

        public static JArray ListJson()
        {
            return new JArray(Tools.Values.OrderBy(t => t.Info.Name).Select(t => new JObject
            {
                ["name"] = t.Info.Name,
                ["description"] = t.Info.Description,
                ["inputSchema"] = t.Schema,
            }));
        }

        /// <summary>Called from an HTTP worker thread.</summary>
        public static ToolResult Call(string name, JObject args)
        {
            if (!Tools.TryGetValue(name, out var tool))
                return ToolResult.Error($"Unknown tool '{name}'.");

            args ??= new JObject();
            try
            {
                if (tool.Info.MainThreadOnly)
                    return MainThread.Invoke(() => (ToolResult)tool.Method.Invoke(null, new object[] { args }));
                return (ToolResult)tool.Method.Invoke(null, new object[] { args });
            }
            catch (Exception e)
            {
                while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
                if (e is ReloadingException) return ToolResult.Text(e.Message);
                return ToolResult.Error($"{e.GetType().Name}: {e.Message}");
            }
        }
    }
}
