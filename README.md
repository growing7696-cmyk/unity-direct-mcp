# Unity Direct MCP

**Your Unity Editor *is* the MCP server.** Install one package, click one button, and Claude Code / Cursor / VS Code can see, build and play-test your game.

- **No Python, no Node.js, no account.** The server runs inside the Editor on `127.0.0.1`.
- **8 sharp tools instead of 70.** One of them runs any C# against the full Editor API, so the agent is never stuck waiting for a tool that doesn't exist.
- **Agents that check their own work.** Compile errors, console logs, play mode and screenshots close the loop: write → compile → fix → play → look → fix.
- **Script reloads don't break the session.** Requests in flight get a clean "Unity is reloading" answer instead of a dropped socket, and the server is back in about 2 seconds.

> 한국어 안내: [README.ko.md](README.ko.md)

## Install (2 minutes)

1. Unity → **Window → Package Manager → + → Add package from git URL**
   ```
   https://github.com/growing7696-cmyk/unity-direct-mcp.git?path=/Package
   ```
2. The **Direct MCP** window opens. The dot is green when the server is running.
3. Click **Connect** next to your AI tool. It writes the config into your project:

   | Tool | File written |
   |---|---|
   | Claude Code | `.mcp.json` + the [Claude skill](#claude-skill) in `.claude/skills/` |
   | Cursor | `.cursor/mcp.json` |
   | VS Code (Copilot) | `.vscode/mcp.json` |

4. Open your AI tool in the project folder and ask for something:
   > "Add a player capsule that moves with WASD, put five crates in front of it, then play-test it and show me a screenshot."

Anything else that speaks MCP over HTTP: use the URL shown in the window (`http://127.0.0.1:<port>/mcp`).
With the Claude Code CLI you can also run `claude mcp add --transport http unity http://127.0.0.1:<port>/mcp`.

Requires Unity 2022.3 or newer. Tested on Unity 6000.6 (macOS).

## Tools

| Tool | What the agent gets |
|---|---|
| `unity_status` | Open scenes, play mode, compile state, **last compile errors**, console counts. `wait_seconds` waits for compiling to finish. |
| `scene_hierarchy` | Compact tree: `Player [Rigidbody, PlayerController]`. Paths work for inactive objects too. |
| `inspect_object` | Every serialized field of a GameObject's components, or of an asset (`Assets/...`). |
| `execute_csharp` | Runs C# in the Editor (≈0.3 s compile with Unity's bundled Roslyn). Returns the value plus any `Debug.Log` output. Changes are grouped into one Undo step. |
| `refresh_assets` | Imports files the agent wrote and starts compilation. |
| `play_mode` | Enter / exit / pause / resume. |
| `read_console` | Console messages with stack traces, kept across script reloads. |
| `screenshot` | Renders the game or scene camera and returns the image so the agent can look at it. |

Scripts are written with the agent's own file tools (it already edits files well), then `refresh_assets` + `unity_status` report compile errors back.

## Claude skill

Tools tell Claude what it *can* do; the skill tells it how to do it well. Clicking **Connect** for Claude Code also installs `.claude/skills/unity-direct-mcp/` into your project (or use the **Claude skill → Install** row on its own). It teaches Claude to:

- look before changing anything (status → hierarchy → inspect), instead of guessing object names
- fix compile errors before touching the scene, and wait out script reloads calmly
- use tested `execute_csharp` recipes: Undo-safe object creation, private fields via `SerializedObject`, materials that match the render pipeline (no pink), prefabs, tags/layers, Input System detection, scenes, UI
- play-test and look at a screenshot before saying "done"

Every recipe in [`references/recipes.md`](Package/Skills~/unity-direct-mcp/references/recipes.md) is run against a live Editor before release. When the package updates, the window shows **Update** for the skill.

### Add your own tool

```csharp
using DirectMcp;
using Newtonsoft.Json.Linq;

public static class MyTools
{
    [McpTool("count_enemies", "Number of objects tagged Enemy.")]
    public static ToolResult CountEnemies(JObject args) =>
        ToolResult.Text(GameObject.FindGameObjectsWithTag("Enemy").Length.ToString());
}
```

Any static method with this signature in an Editor assembly is picked up automatically.

## How it works

```
Claude Code / Cursor / VS Code
        │  MCP (Streamable HTTP, JSON-RPC)
        ▼
http://127.0.0.1:<port>/mcp   ← HttpListener inside the Unity Editor
        │  main-thread dispatch
        ▼
Unity Editor API
```

- The port is derived from the project path, so several projects can run side by side.
- On a domain reload, waiting requests are answered first, then the port is released and re-opened by the new domain.
- Console messages and compile results are stored in `SessionState`, so they survive reloads.

## Security

The server only listens on `127.0.0.1` and rejects browser requests from non-local origins. `execute_csharp` can do anything the Editor can do, so only connect AI tools you trust. You can stop the server from the window at any time.

## Development

`TestProject/` is a sandbox Unity project that loads the package from `../Package`, so edits show up immediately. Open it in Unity 6, or run it headless:

```bash
/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity -batchmode -projectPath TestProject -logFile -
```

The Editor log prints `[Direct MCP] Listening on http://127.0.0.1:<port>/mcp`.

## Credits

The idea of running agent-written C# snippets and how results are serialized are adapted from [MCP for Unity](https://github.com/CoplayDev/unity-mcp) (MIT). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## License

MIT
