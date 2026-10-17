# Factory Layout Generator — context for agents

Draw a production line as a blueprint; the engine validates it, routes the conveyor and the
Unity editor turns it into 3D; a CATIA V5 builder makes the same layout with PowerCopies.
A ground-up rebuild of a TMKT57 (LiU) course assignment. Portfolio piece; Kais wants the best
version, not the easiest.
Full story: `docs/PROJECT_HISTORY.md`.

## Decided — don't reopen

- **All code and models are new.** Nothing from the course version's files (CATParts,
  PowerCopies, VB.NET) is reused. Only the ideas carry over: the 500 mm grid, PowerCopy
  instantiation, straight/left/right conveyor modules. Public texts describe the origin only as a
  TMKT57 course assignment, with no names (Kais, 2026-10-17).
- **The engine is the single source of truth.** `layout.json` stores components only as
  input; the conveyor and metrics in it are for reference. The editor, CLI and CATIA builder all
  re-validate and re-route. Never make a host compute layout logic itself.
- **Work zones** (`Component.WorkCells()`): robot = 3×3 envelope (`Reach = 1`); drill = the
  tile in front (its work cell); machine = the tiles along its loading side. Nothing else may
  stand in one, it may not leave the floor, and the conveyor must pass through it. Two robot
  envelopes sharing cells = warning; any other shared work zone = error (an arm hitting a drill
  head). Drill: the belt runs straight through the work cell across the drill's face, and both
  tiles beside it must be free (`E_FRONT_PASSAGE`).
- **Four editor steps** (floor, equipment, conveyor, 3D). The conveyor is computed only from
  step 3 on: Kais found it distracting while placing equipment. `ConveyorRouter.Resolve`
  handles both modes; `Evaluate` checks a hand-drawn path; `Complete` routes on from one.
- **Machines are resizable (1–8)**, rotate about their centre (`RotateInPlace`), carry Label
  and Colour. Floor voids: use `IsFloor`, never `InBounds`, for anything about floor.
- **CATIA is optional, no longer the main claim** (Kais, 2026-10-15). The claim is planning
  value, measured by `docs/USER_TEST.md`.
- **Conveyor = whole-cell modules, 90° turns, no U-turns, no crossings.** Turn cost 2 (in
  straight-cell units). X-ray: straight through along its axis, either direction. Drill: the
  front cell.
- **Classical search, not RL.** Dijkstra over (cell, heading) per segment, then depth-first
  backtracking over station endings (`Branching = 8` covers every ending). Unrouted ≠
  impossible; never claim it is.
- **`RouteChecker` must stay independent of `ConveyorRouter`.** It is how routes are verified.
- **Engine targets netstandard2.0** (Unity + net48 + net9). No records/init/ranges in Core.
  Its build copies the DLL to `unity/Assets/Plugins` — rebuild Core before building Unity.
- **Unity: Built-in pipeline, everything created at runtime, no colliders.** The build forces
  `Standard` and `Sprites/Default` into Always Included Shaders (otherwise magenta). Stroke
  vertex colours must go through `.linear` (linear colour space). `Component` clashes with
  `UnityEngine.Component`: alias it in every script that uses both.
- **PowerCopy contract:** template `<Name>.CATPart`, PowerCopy `PC_<Name>`, inputs `Origin`
  (point), `Direction` (line), `Floor` (plane). Start/Stop/XRay templates include their own belt.
- Versions verified by running: Unity 6000.5.7f1 · .NET SDK 9.0.203 · net48 for CATIA.

## Run (Windows PC)

```powershell
dotnet test tests\FactoryLayout.Core.Tests
dotnet run -c Release --project src\FactoryLayout.Cli -- bench --out docs\results\bench.md
.\tools\build_and_shoot.ps1 [-Frames] [-NoBuild] [-Layout layouts\x.json]   # builds Core + Unity player, screenshots to logs\shots
& "C:\Users\kais1\Projekt\cable-routing-rl\.venv\Scripts\python.exe" tools\make_gif.py logs\shots\demo-line_frames docs\images\blueprint_to_3d.gif --width 800 --crop-panel
```

Player CLI: `-layout f.json -view 3d -screenshot out.png -frames dir -optimize -yaw -pitch -zoom`.
Check visual changes by capturing screenshots and looking at them; the first two Unity bugs
were found exactly that way.

**CATIA is not installed on this PC.** `catia/` compiles and `--dry-run` works; the COM path
is unverified until Kais runs it at LiU. Treat any CATIA claim as unverified until a
`*.build_report.json` from a real run exists.

## State (measured)

36 tests pass. Benchmark (`docs/results/bench.md`, 300 random layouts each, random process
order, drill-passage rule in force): 0 checker rejections anywhere; tool-picks-order success
99/99/97 % (small/medium/large), fixed-order backtracking 74/72/31 %. Not comparable with the
first (pre-drill-rule) table — different layout sets. Demos: machining-cell (L-hall, 2
machines, 21 m), demo-line (17.5 m). Not measured yet: the user test; CATIA anything.
Screenshots of the editor (all steps, tour, a hand-drawn belt) come from the built player.

## Working with Kais

Swedish in chat, English in code/docs/commits. Narrate as you go. He grants freedom — take it,
then walk him through what you did. Be explicit about measured vs estimated vs unverified, and
correct overstated results plainly. When he says to stop and discuss, discuss; don't run things.
