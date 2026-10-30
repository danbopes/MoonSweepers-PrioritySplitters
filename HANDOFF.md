# Priority Splitters — handoff

BepInEx 6 (be.791, IL2CPP) plugin for Moon Sweepers 1.0.4 (Steam app 5335510, Unity 6000.6, URP). Lets each
splitter fill one output first and only overflow to the other when it is backed up, and each merger take one
input first and only let the other in while that input has nothing waiting. Toggled in game per machine with
one key, persisted per save slot, shown by the letter cone on the machine (S on splitters, M on mergers)
leaning toward the priority lane.

Status (2026-10-29): splitters are confirmed in the user's test save (slot 2, seven splitters, six mergers).
Mergers use the same mechanisms (routing via `lastInput`, the M cone cut from `SM_merger.001`, same key and
prompt). Confirmed from the log: all six mergers tracked with their input lanes, toggling with P cycles
left first → right first → round-robin with the toast and prompt, and the choice persists in slot-2.json. The
M cone blob renders correctly offline (`tools/render_blob.py`). Still to be seen in game: the M cone leaning
on a merger view (the first build looked for `Model/SM_merger.001`, but the merger prefab keeps the body mesh
on `Model` itself, so the body is now found by mesh name) and that the lean direction matches the wording
(a wrong direction is a sign flip in `PriorityCone.Lean` for mergers); and the priority belt visibly
winning. Then Nexus packaging and a GitHub repo (danbopes). No equivalent mod exists (checked).

## Environment

- Linux, Proton 11.0. Game: `~/.local/share/Steam/steamapps/common/Moon Sweepers`. Launch options (set):
  `PROTON_LOG=1 WINEDLLOVERRIDES="winhttp=n,b" %command%`.
- BepInEx 6.0.0-be.791 Unity.IL2CPP win-x64 plus the MoonSweepers BepInEx Patcher (Nexus mod #9,
  `BepInEx/patchers/MoonSweepers.ModEngine/`), which generates `BepInEx/interop/*.dll` from the live runtime
  because stock Cpp2IL only handles metadata up to v106 and the game uses v108.
- Game code reference, gitignored, in `tools/decomp/`: `out-cs/DiffableCs/Assembly-CSharp/MoonCleaners/*.cs`
  (signatures, fields, empty bodies) and `out-isil/IsilDump/.../*.txt` (method bodies as ISIL plus
  disassembly; string literals and calls are greppable; `Method: ` lines mark method starts, the `ISIL:`
  section follows the disassembly). Interop signatures: `ilspycmd -t <Type> <game>/BepInEx/interop/Assembly-CSharp.dll`.
  Private game methods are public in the interop, so `WaitingAtEnd`, `BeltEndingAt`, `lastInput` etc. are
  callable directly.
- Python tooling in `tools/.venv` (UnityPy 1.25, numpy) on the NVMe. `/tmp` is a 31 GB tmpfs that filled up
  during this work; keep scratch and venvs out of it (`tools/.tmp/` is the scratch folder).

## Build and test loop

- Source in `src/` (net6.0). `dotnet build src -c Debug` installs `PrioritySplitters.dll` into
  `<game>/BepInEx/plugins/PrioritySplitters/` (GamePath from `Directory.Build.props`).
- The game must be closed to install: `pkill -f 'MoonSweepers\.exe'`, build, `steam steam://rungameid/5335510`.
  The user loads slot 2 and tests; they are present and expect to be asked to look. Each relaunch costs them a
  minute of loading, so batch changes.
- Log: `<game>/BepInEx/LogOutput.log`. `[Logging.Disk] InstantFlushing = true` is set in BepInEx.cfg; keep it,
  or a hang looks like silence. BepInEx recreates the file at launch, so a `tail -F` started before the launch
  can miss the first lines; poll the line count instead. Plugin config
  `BepInEx/config/danbopes.moonsweepers.prioritysplitters.cfg` has CRLF line endings (`sed` with `\r`);
  BepInEx rewrites it on launch and resets unknown enum values.
- Log lines to expect once the save is loaded: `Kind N (Splitter) is a splitter: 1 in, 2 out, scale 1`,
  `Kind N (Merger) is a merger: 2 in, 1 out, ...`, `S cone split loaded`, `M cone split loaded`, then
  `Tracking 7 splitter(s) and N merger(s): #64 in0 [p1=left* p2=right?]; #130 merger out2 [p0=left(55) p1=right*(56)]`
  (`*` = priority, `?` = output with nothing connected, the bracketed numbers on merger inputs are the lanes
  feeding them, empty when nothing feeds the input; vanilla mergers have inputs p0/p1 and output p2).
- A hang: the "Unity Main Thre" process's `/proc/<pid>/wchan` reads `anon_pipe_read` (a wineserver call).
  `futex_wait_multiple` with zero CPU is just the game idling unfocused at the menu. A native crash leaves no
  handler output; the game's "Crash reports" folder under the Wine prefix only notes the session ended
  abnormally. Watch `pgrep -f 'MoonSweepers\.exe'` from a background loop.

## How the game routes (verified in the ISIL dump)

- `MoonWorld.HandOff(belt, path, id)` moves a capsule from a belt (or machine route) end into a machine. It
  has `EnterMachine` inlined; `MoonWorld.txt` holds `EnterMachine` and `GivesWay` as standalone copies of the
  same logic, which are easier to read. Processors (kinds 96, 97) branch off to `FeedProcessor` first.
- Splitters: `EnterMachine` reads the `machineTurns[id]` cursor into `machineOutputs`, refuses when
  `RouteFull(route)` or `!RouteEntranceClear(machine, input, port)`, else `Enter(route, id)`. Route id =
  `-(machine*16 + input*4 + outputPort)` (`MachineRoute`). `BeltSpacing` 0.75, `BeltSpeed` 1.1.
- Mergers: before the cursor, `EnterMachine` looks up `lastInput[id]`, the input that fed the machine last. If
  it equals the arriving input, it walks the other input ports, finds the belt ending at each
  (`BeltEndingAt(pose + rotation * port.position * def.scale)`, conveyors only, matched within 0.05 m), and
  refuses the handoff when `WaitingAtEnd(belt)` is true for any of them. After a successful `Enter` it writes
  `lastInput[id] = input`. That is the vanilla alternation. `WaitingAtEnd(lane)` is true when any rider on the
  lane has `distance >= Length - 0.75`; `BeltPath` sends negative ids to `MachinePath`, so it works for
  machine routes too.
- `RouteFull` is true when some rider on the route is stopped and `(count+1)*0.75 > length`. A fifth capsule on
  a 3.3 route is admitted while moving and then rests 0.3 from the shared entrance: that is the plug the
  pressure logic exists to avoid.
- Riders: `MoonWorld.riders`, `Dictionary<int, Rider>` (struct {belt@0, distance@4, lag@8, moving@0xC,
  placedOn@0x10, placedAt@0x18}, 40 bytes). The interop wrappers for that struct corrupt the stack, so
  `RiderTable.cs` scans the dictionary's raw memory (entries 56 bytes: hash@0 next@4 value@16; array data at
  +0x20; field offsets resolved through `IL2CPP.il2cpp_class_get_field_from_name`). The dictionary has no
  `_entries` array until the first capsule is added, so a world with nothing riding (a new game) scans as
  empty and the entry layout is resolved from the first array that appears; 1.0.0 threw there and disabled
  itself.

## Plugin layout

- `HandOffPatch.cs`: Harmony prefix on `HandOff`. Resolves the target machine; round-robin ones go to vanilla.
  Splitters: `Splitter.Choose` picks the output (priority first unless `RouteFull`, or at capacity and
  pressured downstream) and writes `machineTurns`. Mergers: a capsule from the priority input gets
  `lastInput.Remove(id)` so vanilla never makes it give way; a capsule from another input is refused while
  `Merger.PriorityWaiting` (vanilla's `WaitingAtEnd` on the priority input's lanes) is true, otherwise vanilla
  runs and still alternates among the non-priority inputs. Any exception calls `Fault`, which disables the mod
  and logs "Disabled".
- `StepConveyorsPatch.cs` + `LaneMonitor.cs`: per-frame lane sampling for splitters; a lane counts as backed
  up after a capsule stands still `GraceSeconds` (0.2) and stays so `ReleaseSeconds` (1.0) after it last moved.
- `MachineLayout.cs`: `Machine` (id, pose, flow, priority port, `LeftToRight`, `NextPriority`, `Describe`),
  `Splitter` (`Input`, `AllOutputs` for naming/toggle/cone, `Outputs` = the connected subset vanilla indexes
  `machineTurns` into, `Choose`) and `Merger` (`Output`, `Inputs` with their feeding `Lanes`: the belt ending
  at the port, or the output routes of a tracked machine linked directly with no belt; unfed inputs stay
  listed with no lanes). Every port from the build definition can hold the priority, so it can be set right
  after placing a machine, before any belt exists. Kinds are classified by port counts (1 in ≥2 out =
  splitter, ≥2 in 1 out = merger). Sides are named left/right looking downstream: flow runs from the single
  port (splitter input, merger output) to the centroid of the others, `right = Cross(up, flow)`. Port
  positions are scaled by `def.scale` as vanilla does. Aim point is the midpoint of the first route.
  **The layout is refreshed from `PriorityBehaviour.Update` (`MachineLayout.Poll`), never from `HandOff`:**
  while `MoonWorld.stepping` is true, `BeltStartingAt`/`BeltEndingAt`/`MachineInputAt` cache every position
  they are asked about in `beltStartAt`/`beltEndAt`/`machineInputAt`, a missing belt included, and no
  clearing of those caches was found in the dump, so asking about an unconnected port during a step could
  leave a stale "no belt" answer for a belt built there later. `HandOff` only notes the world instance and
  looks machines up. An early build refreshed inside `HandOff`, which is why a freshly placed splitter showed
  `#126 in0 []` and could not take a priority.
- `Settings.cs`: per-slot JSON `BepInEx/config/PrioritySplitters/slot-<N>.json` (`{"depotId": port | -1}`),
  slot from `MoonSaveSlots.Active`. Splitters and mergers share the file (depot ids are unique). A machine
  with no entry alternates exactly as in vanilla; the user wants new machines never to default to a side (the
  old `Mode`/`MergerMode` defaults were removed for that reason).
- `PriorityBehaviour.cs` (MonoBehaviour on BepInEx's manager object): toggle key, aim (`ReachMeters` 6 m,
  nearest tracked machine to the camera ray; camera from `MoonWalker.viewCamera`), HUD toast
  (`MoonHudNotice.Show`, "Splitter: left first" / "Merger: right first"), the key prompt (the game's
  `MoonActionPromptCard`: `Create(owner, name, 95)`, `Show(key, title)`, `Hide()`, `OthersShowing`; text
  "Change priority (left first)"), and a half-second `PriorityCone.Sync`.
- `PriorityCone.cs`: the cone lean, below. One `Model` per body mesh (splitter, merger).
- `Diagnostics.cs`: `[routing 5s]`, `[lanes]`, `[refused]`, `[topology]` logging behind `DebugRouting`.

## The cones

- The swinging flap in the belt is `MoonMachineBelt.selector`; vanilla `Steer` animates it. Leave it alone.
- The S cone is baked into the body mesh `SM_splitter.001` (119,883 vertices, 6 materials, non-readable), the
  M cone into `SM_merger.001` (118,553 vertices, same materials). The merger model is the splitter mirrored
  along z: the cone sits at z ≈ +0.606 on the splitter and z ≈ -0.606 on the merger, base ring at y 1.018,
  radius 0.103. Materials are flat colours without textures, so there is no shader trick to hide a cone, and
  GPU readback is impossible (`vertexBufferTarget` cannot be changed on a non-readable mesh).
- `tools/extract_cone.py <resources.assets> <splitter|merger> src/Resources/<model>-cone.bin` reads the mesh
  with UnityPy, cuts the cone's triangles out by triangle (submeshes 2 graphite frustum, 4 cyan letters,
  5 brushed tip; region `y>1.0, |x|<0.1`, z in `(0.5, 0.72)` or `(-0.72, -0.5)`; pivot at the base ring
  centre) and writes a deflated blob: a cone-less body that keeps every original vertex at its original
  index, and the cone relative to its pivot. The cut yields 512 graphite and 448 tip triangles on both
  models and 1840 (S) / 1240 (M) letter triangles; a straddling triangle aborts the script. The blobs are
  embedded resources (~780 KB each); `PriorityCone` rebuilds the meshes at runtime (`Il2CppStructArray`
  channels, `SetTriangles` per submesh), swaps the body mesh on each view (`MoonDepotConstruction.ViewOf(id)`
  → `Model/SM_splitter.001` or `Model/SM_merger.001`), adds a "Priority cone" child sharing the body's
  materials, and leans it (`LeanDegrees` 20, about the body's z axis toward the priority port's side, sunk by
  `r·sin(angle)` so the base stays seated). Round-robin stands upright. Views spawn and despawn by distance,
  hence the periodic sync. If the body path is missing the plugin logs the view's children once.
- **Unity 6 mesh LODs**: the index buffer holds every LOD level back to back inside each submesh
  (`m_MeshLodInfo`, 11 levels here) and Unity draws one. Take level 0 only; every coarser level drawn on top
  looks like smeared letters and slivers. The extractor handles this; it cost most of a day to find.
- `tools/render_blob.py <blob> <out-prefix>` software-renders a blob's cone from four sides plus the body around
  the cut, the quickest check after a game update changes the meshes.
- `tools/patch_readable.py` flips the splitter mesh's readable byte in `resources.assets` for diagnosis only
  (then Unity's own decode of the mesh can be read back in game). The backup lives in `tools/backup/` (298 MB,
  never commit); restore it afterwards with `cp -p`. Never use UnityPy's whole-file save on this file: it
  truncated it to 2.6 MB once.
- Raised machines (`splitter_raised`, `merger_raised`) are skipped: no lean, routing still applies.

## Interop and Wine gotchas

- Any OS query on the main thread can deadlock under Wine: `Keyboard.FindKeyOnCurrentKeyboardLayout` did, so
  keys resolve by reflection on `Keyboard`'s per-key properties (`pKey` etc.). Building UI while the world is
  loading did too; the prompt waits for `!MoonSession.Loading && !MoonLoadingScreen.Active` and a
  `MoonPlayerHud`.
- Constructing generic value types for game APIs (`List<ValueTuple<string[], string>>` for
  `MoonKeyRow.Build`) crashed the game natively with no trace. Prefer APIs that take plain references.
- `[HideFromIl2Cpp]` on MonoBehaviour helpers that take or return managed types, or interop warns and may
  misbehave. Il2Cpp null checks: `== null` for Unity objects, `is null` for plain C# objects.
- The interop `Dictionary<int,int>` has no indexer setter: `Remove` then `Add`.
- Bottom HUD rows (`MoonToolbelt.toolLegend`, the builder's rows) are animated every frame; anything positioned
  relative to them lags a frame and jitters. The mid-screen `MoonActionPromptCard` is steady; the user chose it.
  World-space arrows were rejected as ugly; "input 1/2" wording was rejected in favour of left/right.
- `Config.Bind<Color>` has no converter; use strings.

## Known limitations

- Short downstream belts or direct processor links can still plug for one inching cycle.
- Priorities are local to each player's config in co-op.
- A merger input fed directly by a processor (no belt, no tracked machine) has no lane to watch, so that input
  can never be seen as waiting.
