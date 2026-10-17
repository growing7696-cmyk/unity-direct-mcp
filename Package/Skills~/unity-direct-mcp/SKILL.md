---
name: unity-direct-mcp
description: Build, edit and play-test Unity projects through the Unity Direct MCP tools (unity_status, scene_hierarchy, inspect_object, execute_csharp, refresh_assets, play_mode, read_console, screenshot). Use this whenever the user asks for anything inside a Unity project or scene - making a game, adding a player/enemy/level/UI, changing GameObjects, components, prefabs, materials or scripts, fixing Unity compile or runtime errors, or "check if it works" - even if they never mention MCP. Covers the look-before-you-change workflow, safe C# recipes for the Editor API, and the compile/reload/play-test loop.
---

# Unity Direct MCP

You are driving a live Unity Editor. The tools give you eyes (status, hierarchy, inspect, console, screenshot) and hands (execute_csharp, your own file edits, play mode). Agents that skip the eyes guess object names, write code against fields that don't exist, and report success on games that don't run. The loop below exists to prevent that.

## The loop

1. **Look first.** `unity_status` (Unity version, render pipeline, open scene, compile state), then `scene_hierarchy`. Before changing a component, `inspect_object` it — field names in the Inspector are not always the C# names (`m_Mass` is `mass`).
2. **Write scripts as files** under `Assets/Scripts/` with your normal file tools. One MonoBehaviour per file, file name = class name.
3. **Compile.** `refresh_assets`, then `unity_status` with `wait_seconds: 60`. If `last_compile.succeeded` is false, fix every error before anything else — Unity will not enter play mode or load new types while scripts are broken.
4. **Wire up the scene** with `execute_csharp`: create objects, add your components, assign references, make prefabs and materials, then save (see recipes).
5. **Verify.** `screenshot` to check layout and camera framing. `play_mode` enter → `unity_status` with `wait_seconds: 30` → wait a moment (`sleep 3` in the shell) → `read_console` types `["error","warning"]` and `screenshot` → `play_mode` exit.
6. **Report honestly**: what you checked, what you saw, what you could not verify (e.g. input feel, UI overlay — screenshots don't include Screen Space Overlay canvases).

## Script reloads

After a successful compile and when entering play mode, Unity reloads all scripts for 2–10 seconds. During that window a call may return `"reloading": true` or fail to connect. That is normal: wait 3–5 seconds and call `unity_status` again. Don't retry in a tight loop and don't assume the server is broken.

## execute_csharp essentials

- The code is a method body returning `object`; end with `return something;` so you see the outcome. `Debug.Log` output comes back too.
- Default usings include `System.Linq`, `System.IO`, `System.Reflection`, `UnityEngine`, `UnityEditor`, `UnityEngine.SceneManagement`, `UnityEditor.SceneManagement`. Add others with the `usings` argument (e.g. `["UnityEngine.UI", "TMPro"]`).
- Your project's own classes (e.g. `PlayerController`) are available once they compile.
- Find objects with `DirectMcp.Tools.ObjectPaths.Find("Parent/Child")`. `GameObject.Find` skips inactive objects and returns the first match by name, which is a common source of "it changed the wrong thing".
- Register changes with Undo (`Undo.RegisterCreatedObjectUndo`, `Undo.AddComponent`, `Undo.RecordObject`). Each call becomes one Ctrl+Z step for the user, so they can back out of anything you did.
- Pass `read_only: true` for pure queries so the scene isn't marked modified.
- Changes made while in play mode are thrown away when play mode ends. Make permanent edits in edit mode.
- Save before you finish: `EditorSceneManager.SaveOpenScenes(); AssetDatabase.SaveAssets();`

Ready-made snippets for the common jobs (objects, components and private fields, materials per render pipeline, prefabs, folders, tags/layers, input system detection, scenes and build settings, UI) are in [references/recipes.md](references/recipes.md). Read it before your first `execute_csharp` call in a session; it saves a lot of failed compiles.

## Things that commonly go wrong

- **Pink objects**: the material's shader doesn't match the render pipeline. Check `render_pipeline` in `unity_status` and use the material recipe.
- **Input does nothing**: the project uses the Input System package but the script uses `Input.GetAxis` (or the reverse). Check with the input recipe before writing movement code.
- **`NullReferenceException` on play**: a serialized reference was never assigned. Assign it with `SerializedObject` in the scene, or look it up in `Awake`.
- **Camera sees nothing**: no camera tagged `MainCamera`, or it points away. Use `screenshot` after placing things.
- **Physics objects fall through the floor**: the floor has no collider, or the moving object has a Rigidbody without a collider.
- **Edits vanish**: the scene wasn't saved, or changes were made in play mode.

## Scope

Treat the user's project with care: don't delete assets or scenes you didn't create unless asked, and say what you changed. `execute_csharp` has full Editor access, so prefer the smallest change that does the job.
