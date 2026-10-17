# CATIA V5 builder (optional)

> Since 15 October 2026 this is an optional export, not the project's main path. The value of
> the tool is in planning, which the Unity editor covers; see `docs/USER_TEST.md` for how
> that is measured.

`FactoryLayout.Catia.exe` turns a `layout.json` from the editor into a CATPart in CATIA V5. It
is the CAD half of the project. Everything else runs anywhere; this half needs a computer with
CATIA V5, which in practice means a LiU lab PC.

> **Status: compiled, not yet run against CATIA.** The plan it builds from (`--dry-run`,
> `CadPlan` in the engine) is unit-tested. The COM calls follow the course project's working
> VB.NET code (same `InstanceFactory` sequence, same `PutInputData` on objects in the target
> part) and the V5 automation reference, but no CATIA was available when it was written. The first run at LiU is
> the test. Note what fails in `<layout>.build_report.json`.

## How it builds

1. The engine re-validates and re-routes the layout, so the CAD model cannot disagree with
   the editor.
2. `CadPlan` lists every part: template, origin (cell centre, mm) and direction. These are the
   components plus the conveyor modules. The belt inside the X-ray tunnel and on the Start and
   Stop cells belongs to those templates.
3. In one new CATPart:
   - **Floor**: a 20 mm slab (pad, downwards from z = 0) and a *Floor grid* geometric set.
   - **Layout inputs**: per part, a point `<id>_Origin` and a line `<id>_Direction` (half a
     tile long, in the facing / travel direction).
   - **One PowerCopy instantiation per part**, on those inputs. This is the course project's
     idea, kept. What changed is the bookkeeping: the course version created a new CATPart for
     every floor tile, copied reference geometry into it and moved it with parameters. Here all
     inputs sit in one part, the floor is one pad, and positions come from the inputs.
   - A part whose template file is missing becomes a **block** (its footprint padded to its
     height, one coloured body per type), so a model comes out even with no templates.
4. Saves `<layout>.CATPart` and writes `<layout>.build_report.json` with counts and seconds.

## Run it at LiU

Copy `catia\FactoryLayout.Catia\bin\Release\net48\` (the exe plus `FactoryLayout.Core.dll`) and
your layout to the lab PC. Start CATIA, then:

```
FactoryLayout.Catia.exe demo-line.json --boxes          # first run: blocks only, no templates needed
FactoryLayout.Catia.exe demo-line.json                  # with templates\*.CATPart next to the layout
FactoryLayout.Catia.exe demo-line.json --dry-run        # no CATIA: writes demo-line.plan.csv
```

`--boxes` first. It exercises the floor, the inputs and sketch/pad without any PowerCopy, so if
it fails the problem is in the basic calls and not in a template.

## Templates (make them once, in CATIA)

A template is a CATPart named after the plan's template, containing a PowerCopy named
`PC_<Template>` with exactly these inputs, **in these names**:

| Input | Type | Meaning |
|---|---|---|
| `Origin` | point | centre of the part's first cell, on the floor |
| `Direction` | line | from `Origin`, in the facing / travel direction |
| `Floor` | plane | the floor (the builder passes the part's xy plane) |

Build the geometry relative to these three only, e.g. an axis system made from `Origin`, `Floor`
and `Direction`. Then anything built on it follows. The cell is 500 mm unless the layout says
otherwise.

| Template | Footprint (along Direction × across) | Notes |
|---|---|---|
| `Start` | 1 × 1 cell | infeed end, **including the belt on its cell**; Direction = where the belt leaves |
| `Stop` | 1 × 1 cell | outfeed end, including the belt on its cell; Direction = where the belt arrives from |
| `ConveyorStraight` | 1 × 1 cell | belt top 800 mm, belt 0.72 cell wide, centred |
| `ConveyorLeft` | 1 × 1 cell | enters against Direction's back edge, leaves through the **left** edge (90°, radius ½ cell) |
| `ConveyorRight` | 1 × 1 cell | as Left, leaves through the **right** edge |
| `Robot` | 1 × 1 cell body | stands on one cell; its 3×3 work envelope is kept clear by the editor, not modelled |
| `Drill` | 1 × 1 cell | works on the cell in front: the head overhangs the next cell along Direction |
| `XRay` | 2 × 1 cells | tunnel from `Origin` forward two cells, **including the belt inside** |
| `Pillar` | 1 × 1 cell | building column |

Left and right are as seen from above, looking along Direction.

Make your own models for these; nothing from the course version is reused. Simple shapes are
enough: the point is the automation.

## The time measurement (for the portfolio)

The build report gives the script's time. The manual baseline has to be measured by hand.
Nothing in this repo estimates it:

1. In CATIA, open `templates` and an empty part. Start a stopwatch.
2. Place the parts of `demo-line.plan.csv` by hand, the way you would without the tool:
   instantiate each PowerCopy and pick its inputs. Use the plan's positions, so that only the
   placing is timed and not the design thinking.
3. Stop when the last part is in. Note the time and how many parts you placed. If you stop
   early, note how many you got through and scale. Say that you scaled.
4. Run the builder on the same layout and note `seconds.total` from the report.

Report both numbers and the part count (43 for `demo-line`). Do not report a speed-up without
the manual time behind it.
