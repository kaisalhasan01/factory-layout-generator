# Project history — from a course form to a layout engine

The running record of the project: where it started, what was wrong, each decision and why,
and what exists now. Written so that someone can pick it up cold, and so that it can be cut
down into a CV or portfolio entry without working anything out again. Same format as the
cable-routing project's history.

**Timeline:** course project TMKT57 spring 2025 · audit and rebuild 14 October 2026 · first
hands-on test and second version 15 October 2026.

---

## 1. What the project is

A tool for laying out a production line. You draw the factory floor as a blueprint: start, stop,
robot arms, machines and building columns. The tool checks the layout, routes the conveyor
through every station automatically, and turns the drawing into a 3D line. The same layout
file then drives CATIA V5, which instantiates one PowerCopy per part.

The idea comes from a course assignment in **TMKT57 — Product Modelling**, Linköping
University. This version is a ground-up rebuild by Kais Alhasan.

## 2. The starting point

The course version was three VB.NET Windows Forms projects and a written report. An audit
of them, in short:

- **The final version placed a floor, a start, a stop and robots, and no conveyor.** "Turn left"
  failed on its second piece: the new part was named with the *text* `"Button567Counter"`, and
  its references were looked up in a document that never exists. "Straight" and "Right" were
  empty methods. The report says the same: the conveyor was tried three ways and postponed.
- Each floor tile was its own CATPart: copy a reference point, paste, instantiate a PowerCopy,
  move it with parameters. A 20 × 15 floor is 300 documents.
- Hard-coded paths to a LiU network drive, rebuilds that left parts behind, no input checks,
  and a crash without a message when CATIA was not running.
- An earlier version with a clickable grid did not compile.

**Root cause:** the layout existed only inside CATIA. With no model of the layout outside it,
there was nowhere to check a layout, no way to route a conveyor except piece by piece by hand,
and nothing could be tested without CATIA open. So nothing was tested.

## 3. Decisions, and why

| Decision | Why |
|---|---|
| **Separate the layout from the CAD system.** An engine, a file contract (`layout.json`), an editor and a CATIA builder. | The cable project's lesson: the logic was tangled into the tool that ran it. CATIA is not on the development PC, so a CATIA-only tool cannot be tested or measured. A Unity-only tool would lose the point, CAD automation. |
| **Engine in C#, netstandard2.0.** | One DLL loads in Unity, in .NET Framework 4.8 (the CATIA builder, which runs on a stock lab PC) and in .NET 9 (tests, CLI). |
| **Unity for the editor.** | Already installed and known from the cable project. Gives the "draw a blueprint, see it in 3D" step the course version planned and dropped. |
| **All code and models new.** Nothing from the course version's files is reused; only the ideas are: the 500 mm grid, PowerCopy instantiation, straight/left/right conveyor modules. | A clean start, the same way as the cable-routing rebuild. |
| **A robot arm stands on one cell and owns the 3×3 around it.** Nothing else may stand there and it may not reach past the wall, but the conveyor must pass through it. | Kais's requirement: a real arm needs room to move, about 9 tiles. The conveyor has to come within reach or the arm has nothing to pick. |
| **Conveyor modules are whole cells with 90° turns.** No U-turns, no crossings. | Kais's requirement, and how modular conveyors are bought. It maps one to one onto the straight, left and right modules. |
| **Routing by classical search, not learning.** | The conveyor is the cable problem in 2D, but here exact search is cheap. The cable project's own conclusion was that a planner beats a learned policy when one is affordable. |
| **PowerCopy kept, bookkeeping changed.** One CATPart, one point plus one line per part as inputs, one instantiation each. | Keeps the course idea Kais asked to keep, without one document per tile. |

## 4. The build, in order

### 4.1 Engine

`src/FactoryLayout.Core`. Layout model (grid, components with a body, a facing and a work
envelope), validator, router, independent route checker, metrics, JSON contract, CAD plan,
blueprint SVG.

**Routing.** Dijkstra over (cell, heading). A step costs 1 and a turn costs 2 more, so the
router prefers fewer turn modules. The conveyor is routed segment by segment, Start → each
station → Stop, and cells already laid are blocked, like the trail in the cable project. What
counts as reaching a station depends on its type:

- Robot: any free cell of its envelope.
- Drill: the cell it faces.
- X-ray: the cell behind or in front of the tunnel, then straight through it, in either
  direction.

**First measurement, and the fix.** Greedy segments (the cheapest path to each station)
routed only 138/300 random layouts with a fixed process order. With the station order left to
the tool, 287/300 routed. So most failures were not impossible layouts: the conveyor walled
itself in on the way to one station and then could not reach the next. Fix: depth-first
backtracking over each station's endings, cheapest first. Endings tried per station, and the
result:

- 1 ending: 138/300
- 3 endings: 168/300
- 8 endings: 185/300
- 16 endings: 185/300

It levels off at 8 because 8 covers every ending (a robot has 8 envelope cells). The rest are
**not proven impossible**: other paths to the same ending are not explored. They are reported
as not routed.

**Order search.** Up to 6 stations, every order is tried: the placement order with full
backtracking, the rest greedily, pruned by the best cost so far. Above 6 stations, a pairwise
swap search. This took a test from 60 s to 2 s at no loss.

**Independent checker.** `RouteChecker` re-checks every route without using the router's
code: one step at a time, no repeats, no U-turns, never through a body except Start, Stop and a
tunnel run straight along its axis, every station served in order. No route it has checked has
failed (`docs/results/bench.md`, column "rejected").

### 4.2 Editor (Unity 6000.5.7f1)

`unity/`, with the scene generated by `Assets/Editor/FactoryBuild.cs`. Everything visible is
built at runtime from primitives. No imported models, no physics.

- Blueprint: click to place, drag to move, R rotates, right-click deletes, Ctrl+Z undoes.
  Validation and routing run on every edit. Hovering shows the footprint and the robot's 3×3,
  red where it would not fit. The process order is shown and can be reordered, and the tool can
  suggest an order.
- **Tab**: the drawing takes 3D form. A dolly zoom from a top-down 2° lens to a 42° perspective
  keeps the floor the same size on screen while the camera tilts. The paper turns to concrete
  and the parts grow from the floor in a wave. Then boxes ride the conveyor and the arms work.
- Save writes `layout.json` plus a blueprint SVG.

Two bugs found by looking at the first screenshots, both fixed:

- Vertex colours are not gamma-corrected in linear colour space, so every stroke rendered
  lighter than intended.
- Thick polylines drawn as separate segments gave the conveyor saw-toothed edges. It is now
  drawn as one mitred strip.

### 4.3 CATIA builder

`catia/FactoryLayout.Catia`, .NET Framework 4.8, late-bound COM. Re-routes the layout,
builds the floor and inputs, then one PowerCopy per part or a block where a template is
missing. Writes a timing report. **Compiled; not yet run against CATIA.** See
`catia/README.md`.

### 4.4 First hands-on test, and what it changed (15 October 2026)

Kais ran the editor and came back with eight points. Each one, and what was done:

| # | Feedback | Change |
|---|---|---|
| 1 | The panel was too small to read, even full screen. | The whole interface is redrawn and scales: automatically with screen height, plus `A−`/`A+` and `Ctrl` `+`/`−` (remembered between runs). Flat dark theme, larger type, a status bar describing the tile under the cursor. |
| 2 | The drill reaches over the belt but only reserved its own tile, so a robot arm could swing into it. | The tile in front of a drill is its **work cell**. The belt must run straight through it, across the drill's face. No robot envelope may reach in (`E_WORKZONE_CONFLICT`). Both tiles beside it must be free, so a straight passage exists (`E_FRONT_PASSAGE`). Robot envelopes, drill work cells and machine loading sides are now one concept: work zones. |
| 3 | Real halls are not rectangles. | Step 1 paints floor tiles out and back in (voids), for columns, shafts, offices, an L-shaped hall. Sizes are entered in metres. The grid stays 0.5 m (the conveyor module), so 3.5 × 3.5 m is 7 × 7 tiles. In 3D the slab is built row by row, so removed tiles are real holes. |
| 4 | The auto-routed belt appeared as soon as Start and Stop existed, and jumped around with every machine placed. | The editor has four steps, and the belt exists only from step 3. There, choose **route automatically** or **draw it myself**. A drawn belt is checked as it grows: partial paths say what is still missing. **Finish automatically** routes on from the end of the drawing, and **Edit this route by hand** turns an automatic route into a drawn one. |
| 5 | A tutorial. | A nine-page guided tour on first start. It dims everything but the part it explains, can be skipped, and comes back with `?`. |
| 6 | Machining cells at Scania are large enclosures of varying size; the belt runs past and one product at a time is taken in. | A **Machine** tool: any size from 1×1 to 8×8 tiles, own name and colour, loaded from one side (drawn yellow). In 3D: an enclosure with a loading hatch, a window, a control panel and a stack light. Large machines rotate about their centre. |
| 7 | Interactive, easy to understand, professional, helps with the problems that come up. | Every validation issue now carries a one-line fix. Clicking an issue pulses its tiles on the drawing. Undo and redo. Hover descriptions. |
| 8 | Is CATIA needed at all? | No longer the main path. The value is in planning: catching mistakes before they reach the floor. The CATIA builder stays in the repo as an optional export, marked unverified. The time-saving claim is replaced by a user-test protocol (`docs/USER_TEST.md`) that measures planning time and errors against pen and paper. |

What the stricter drill rule did to the benchmark, in order:
1. Requiring the belt to pass straight under the drill made fixed-order success on random
   layouts fall (185 → 147 of 300 in the test set).
2. Order search fell to about 70%. The cause was layouts where the drill's work cell had no
   straight passage at all. The router could only discover that in step 3; the user would
   see nothing earlier.
3. That became a validation rule (`E_FRONT_PASSAGE`), shown the moment the drill is placed.
   With it the generator no longer produces those layouts, and the benchmark is a new,
   stricter set: 99/99/97% with order search.

The first table and the new one are different layout sets, and the README says so.

Two things were checked by screenshot rather than by test:
- the drawn-belt display: a deliberately wrong path, turning under the drill, is flagged in
  red with the reason;
- the tour overlay.

Mouse drawing itself has no automated test: the player has no input injection. Its logic sits
on `ConveyorRouter.Evaluate`/`Complete` and `RouteChecker`, which are tested.

## 5. What exists today

- Engine with 36 unit tests (`dotnet test`).
- CLI: `route` (metrics, blueprint SVG, routed JSON) and `bench`.
- Unity editor in four steps (floor, equipment, conveyor, 3D), with a guided tour, scalable
  interface, hand-drawn or routed conveyor, custom machines and free-form floors. Buildable by
  script (`tools/build_and_shoot.ps1`), with unattended screenshots, tour pages and
  transition frames (`-screenshot`, `-tour`, `-frames`).
- CATIA builder (optional) with `--dry-run`, `--boxes` and PowerCopy modes, plus template spec.
- Demo layouts:
  - `layouts/machining-cell.json`: an L-shaped 11 × 6.5 m hall, CNC mill 3×3, lathe 2×2,
    2 robots, drill, X-ray. 21 m conveyor.
  - `layouts/demo-line.json`: 9 × 5.5 m, 3 robots, drill, X-ray. 17.5 m.

## 6. Verified vs. unverified — read before quoting anything

| Claim | Status |
|---|---|
| Validation rules (envelope, work zones, overlap, wall, voids, drill passage, tunnel ends, machine side) | Verified: unit tests |
| Hand-drawn belts: partial paths, wrong turns, auto-completion keeps the drawn part | Verified: unit tests; display checked by screenshot |
| Every successful route obeys the rules | Verified on the benchmark sets by the independent checker |
| Routing success rates and times | Measured: `docs/results/bench.md`, random layouts with random process order |
| Unrouted layouts are impossible | **Not claimed.** The search is not exhaustive over paths |
| Editor works and the morph looks as described | Verified: screenshots and frames from the built player |
| CAD plan (what to instantiate, where, facing which way) | Verified: unit tests and `--dry-run` |
| CATIA builder creates the model | **Unverified.** Compiled only; needs a LiU lab PC |
| Planning is faster and has fewer mistakes than by hand | **Not measured.** Protocol in `docs/USER_TEST.md` |
| Time saved against manual CAD layout | **Not measured.** Protocol in `catia/README.md`; no longer the main claim |

## 7. Next steps

1. **Run the user test** (`docs/USER_TEST.md`) with 4–6 people. That gives the portfolio its
   result: planning time and mistakes, by hand against the tool.
2. Operator walkways and safety fences as their own zone type, and the distances they need.
3. A STEP or glTF export, so the 3D layout opens in any CAD system or viewer.
4. Optional: the CATIA builder at LiU (`catia/README.md`).

## 8. CV / portfolio material

**Factory layout generator — plan a production line, see it in 3D.** Rebuilt from the ground up a TMKT57
course assignment whose CATIA form could place machines but never a conveyor. The new version separates
the layout logic from CAD:

- a C# engine validates layouts against real constraints (a robot arm owns its 3×3 work
  envelope) and routes the conveyor automatically through every station with 90° modules;
- a Unity editor where a drawn blueprint takes 3D form;
- optionally, a CATIA V5 builder that instantiates one PowerCopy per part from the same file
  (compiled, not yet run against CATIA).

The editor plans a line in four checked steps:
- free-form floors;
- custom machine sizes;
- robot envelopes and drill work cells that cannot collide;
- a conveyor that routes itself or is drawn by hand and checked as it is drawn;
- a guided tour.

Measured on random layouts:
- 100% of successful routes pass an independent rule check;
- routing takes milliseconds;
- backtracking lifts fixed-order success from 47% to 72% on the medium set;
- with order search, 97–99%.

*(Fill in after the user test: planning time and mistakes, by hand against the tool.)*
