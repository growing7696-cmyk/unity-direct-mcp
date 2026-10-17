# execute_csharp recipes

Each block is a complete `code` argument for `execute_csharp`. Adapt names and values; keep the Undo and save calls.

Contents: [Folders](#folders) · [Objects](#objects) · [Components and private fields](#components-and-private-fields) · [Materials](#materials) · [Prefabs](#prefabs) · [Tags and layers](#tags-and-layers) · [Input system](#input-system) · [Scenes and build settings](#scenes-and-build-settings) · [UI canvas](#ui-canvas) · [Queries](#queries)

## Folders

```csharp
string EnsureFolder(string path)
{
    var parts = path.Split('/');
    var current = parts[0];                      // "Assets"
    for (int i = 1; i < parts.Length; i++)
    {
        var next = current + "/" + parts[i];
        if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
        current = next;
    }
    return current;
}
return EnsureFolder("Assets/Game/Materials");
```

Use `AssetDatabase.CreateFolder` rather than `Directory.CreateDirectory`: the asset database then knows the folder exists and `CreateAsset` into it works immediately.

## Objects

```csharp
var root = new GameObject("Level");
Undo.RegisterCreatedObjectUndo(root, "Create level");

var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
Undo.RegisterCreatedObjectUndo(floor, "Create floor");
floor.name = "Floor";
floor.transform.SetParent(root.transform);
floor.transform.localScale = new Vector3(4, 1, 4);   // a Plane is 10x10 units at scale 1

for (int i = 0; i < 5; i++)
{
    var crate = GameObject.CreatePrimitive(PrimitiveType.Cube);
    Undo.RegisterCreatedObjectUndo(crate, "Create crate");
    crate.name = "Crate" + i;
    crate.transform.SetParent(root.transform);
    crate.transform.position = new Vector3(i * 2 - 4, 0.5f, 5);   // y = half height so it sits on the floor
}
return root;
```

Moving or changing an existing object:

```csharp
var player = DirectMcp.Tools.ObjectPaths.Find("Player");
if (player == null) return "Player not found";
Undo.RecordObject(player.transform, "Move player");
player.transform.position = new Vector3(0, 1, -3);
return player.transform.position;
```

## Components and private fields

Add a component, then set fields the way the Inspector does. `SerializedObject` works for `[SerializeField] private` fields too and marks things dirty correctly.

```csharp
var player = DirectMcp.Tools.ObjectPaths.Find("Player");
var rb = player.GetComponent<Rigidbody>() ?? Undo.AddComponent<Rigidbody>(player);
rb.constraints = RigidbodyConstraints.FreezeRotation;

var controller = Undo.AddComponent<PlayerController>(player);   // your compiled script
var so = new SerializedObject(controller);
so.FindProperty("moveSpeed").floatValue = 6f;                         // field name as written in C#
so.FindProperty("cameraTransform").objectReferenceValue = Camera.main.transform;
so.ApplyModifiedProperties();
return controller;
```

If `FindProperty` returns null, the name is wrong or the field isn't serialized. `inspect_object` lists the real serialized names.

## Materials

Pick the shader from the active render pipeline; a Built-in shader in a URP/HDRP project renders pink.

```csharp
Shader LitShader()
{
    var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
    if (rp == null) return Shader.Find("Standard");                       // Built-in
    if (rp.defaultMaterial != null) return rp.defaultMaterial.shader;      // URP Lit / HDRP Lit
    return Shader.Find("Universal Render Pipeline/Lit");
}

if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
var mat = new Material(LitShader()) { color = new Color(0.9f, 0.3f, 0.2f) };   // .color maps to _Color or _BaseColor
AssetDatabase.CreateAsset(mat, "Assets/Materials/Crate.mat");

foreach (Transform t in DirectMcp.Tools.ObjectPaths.Find("Level").transform)
{
    if (!t.name.StartsWith("Crate")) continue;
    var r = t.GetComponent<Renderer>();
    Undo.RecordObject(r, "Assign material");
    r.sharedMaterial = mat;
}
AssetDatabase.SaveAssets();
return mat;
```

Save materials as assets (as above). A `new Material` that only lives in the scene is lost when the scene reloads.

## Prefabs

Turn a scene object into a prefab, keeping the scene object linked to it:

```csharp
if (!AssetDatabase.IsValidFolder("Assets/Prefabs")) AssetDatabase.CreateFolder("Assets", "Prefabs");
var enemy = DirectMcp.Tools.ObjectPaths.Find("Enemy");
var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(enemy, "Assets/Prefabs/Enemy.prefab", InteractionMode.UserAction);
return prefab;
```

Place prefab instances (keeps the prefab link, unlike `Object.Instantiate`):

```csharp
var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Enemy.prefab");
var parent = DirectMcp.Tools.ObjectPaths.Find("Enemies") ?? new GameObject("Enemies");
for (int i = 0; i < 3; i++)
{
    var e = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.transform);
    Undo.RegisterCreatedObjectUndo(e, "Place enemy");
    e.transform.position = new Vector3(-4 + i * 4, 0, 10);
}
return parent;
```

Edit the prefab asset itself (all instances update):

```csharp
var path = "Assets/Prefabs/Enemy.prefab";
var root = PrefabUtility.LoadPrefabContents(path);
try
{
    if (root.GetComponent<Rigidbody>() == null) root.AddComponent<Rigidbody>();
    PrefabUtility.SaveAsPrefabAsset(root, path);
}
finally { PrefabUtility.UnloadPrefabContents(root); }
return "updated";
```

## Tags and layers

Tags must exist before `go.tag = "Enemy"` works:

```csharp
void EnsureTag(string tag)
{
    if (UnityEditorInternal.InternalEditorUtility.tags.Contains(tag)) return;
    UnityEditorInternal.InternalEditorUtility.AddTag(tag);
}
EnsureTag("Enemy");

int EnsureLayer(string name)
{
    int existing = LayerMask.NameToLayer(name);
    if (existing >= 0) return existing;
    var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
    var layers = tagManager.FindProperty("layers");
    for (int i = 8; i < layers.arraySize; i++)              // 0-7 are built-in
    {
        var p = layers.GetArrayElementAtIndex(i);
        if (string.IsNullOrEmpty(p.stringValue)) { p.stringValue = name; tagManager.ApplyModifiedProperties(); return i; }
    }
    return -1;
}
return EnsureLayer("Ground");
```

## Input system

Check before writing movement code:

```csharp
var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
int handler = settings.FindProperty("activeInputHandler").intValue;   // 0 = old Input Manager, 1 = Input System package, 2 = both
bool packageInstalled = File.ReadAllText("Packages/manifest.json").Contains("\"com.unity.inputsystem\"");
return new { handler, packageInstalled };
```

- `handler` 0 or 2 → `Input.GetAxis("Horizontal")`, `Input.GetKeyDown(KeyCode.Space)` work.
- `handler` 1 → use `UnityEngine.InputSystem`: `Keyboard.current.wKey.isPressed`, `Mouse.current.delta.ReadValue()`. The old `Input` class throws `InvalidOperationException` at runtime.

## Scenes and build settings

```csharp
if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets", "Scenes");
var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);   // camera + light
EditorSceneManager.SaveScene(scene, "Assets/Scenes/Level1.unity");

var list = EditorBuildSettings.scenes.ToList();
if (!list.Any(s => s.path == scene.path)) list.Add(new EditorBuildSettingsScene(scene.path, true));
EditorBuildSettings.scenes = list.ToArray();
return scene.path;
```

`NewScene` with `NewSceneMode.Single` closes the current scene. Save it first if it has changes (`EditorSceneManager.SaveOpenScenes()`).

## UI canvas

Needs the `com.unity.ugui` package (most templates include it; check `Packages/manifest.json`) and `usings: ["UnityEngine.UI", "UnityEngine.EventSystems"]`. For text, prefer TextMeshPro (`TMPro.TextMeshProUGUI`) if the project has it; otherwise `Text` with the built-in font below.

```csharp
var canvasGo = new GameObject("HUD", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
Undo.RegisterCreatedObjectUndo(canvasGo, "Create HUD");
canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
var scaler = canvasGo.GetComponent<CanvasScaler>();
scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
scaler.referenceResolution = new Vector2(1920, 1080);

if (Object.FindFirstObjectByType<EventSystem>() == null)
{
    var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));   // InputSystemUIInputModule if handler == 1
    Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
}

var label = new GameObject("Score", typeof(RectTransform), typeof(Text));
label.transform.SetParent(canvasGo.transform, false);
var rt = label.GetComponent<RectTransform>();
rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);   // top-left
rt.anchoredPosition = new Vector2(40, -40);
rt.sizeDelta = new Vector2(400, 80);
var text = label.GetComponent<Text>();
text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
text.fontSize = 48;
text.text = "Score: 0";
return canvasGo;
```

Screen Space Overlay canvases don't show up in `screenshot` (it renders cameras). To check UI layout visually, temporarily set `renderMode = RenderMode.ScreenSpaceCamera` with `worldCamera = Camera.main`, take the screenshot, then switch back.

## Queries

Use `read_only: true` for these.

```csharp
// Every object with a given component, with paths
return Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.None)
    .Select(r => DirectMcp.Tools.ObjectPaths.PathOf(r.gameObject)).ToList();
```

```csharp
// Assets of a type
return AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).ToList();
```

```csharp
// Values at runtime (in play mode)
var enemy = DirectMcp.Tools.ObjectPaths.Find("Enemies/Enemy");
return new { pos = enemy.transform.position, velocity = enemy.GetComponent<Rigidbody>().linearVelocity };
```
