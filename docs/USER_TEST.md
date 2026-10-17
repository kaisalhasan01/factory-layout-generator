# User test: does the tool make line planning faster and less error-prone?

The tool's claim is not that it places parts in CAD faster. The claim is that a plan made with
it has fewer mistakes, and is reached sooner, than a plan made the usual way. Mistakes are a
robot arm with no room to swing, a drill head over a conveyor bend, or a conveyor that cannot
reach a station. This protocol measures that claim. Until it has been run, the project makes
no time-saving claim.

## Design

- **Participants:** 4–6 people. Course mates are fine. At least one should have shop-floor
  experience (production line, assembly).
- **Two conditions**, each participant does both:
  - **A — by hand:** squared paper (1 square = 0.5 m), pencil, eraser.
  - **B — the tool:** Factory Layout, after its built-in tour.
- **Two equivalent tasks**, so nobody plans the same line twice. Half the participants do
  A-task 1 then B-task 2; the other half do B-task 1 then A-task 2. This balances learning
  and fatigue.
- **No help** during a task. Answer only questions about what the task says.

## The tasks

Both tasks list the hall, the equipment and the rules. The participant has to fit everything
in and lay a conveyor from IN through every station in the given order to OUT.

**Task 1** (hall 11 × 6.5 m, L-shaped: the 3 × 2 m corner at the back right is an office):
IN and OUT on opposite short walls; CNC mill 1.5 × 1.5 m; robot arm; drill; lathe 1 × 1 m;
X-ray tunnel 1 m long; building columns at 3 m spacing along the middle. Process order: mill →
robot → drill → robot → lathe → X-ray.

**Task 2** (hall 9 × 7 m, a 1 × 1 m shaft in the middle): IN and OUT on the same long wall;
press 2 × 1.5 m; two robot arms; drill; X-ray tunnel 1 m long; columns at 4.5 m spacing.
Process order: robot → press → drill → robot → X-ray.

**Rules given to everyone, on paper, in both conditions:**
- A robot arm stands on one tile and needs the 3 × 3 tiles around it free of other equipment.
  The conveyor must pass through that area.
- A drill works on the tile in front of it, over the conveyor. The conveyor runs straight
  through that tile, and no robot may reach into it.
- A machine is loaded from one side, and the conveyor must pass along that side.
- The conveyor uses whole tiles and 90° turns only. It never crosses itself.

## What to measure

| Measure | How |
|---|---|
| **Time to a finished plan** | Stopwatch, from reading the task to "done". |
| **Errors in the plan** | Transcribe every paper plan into the tool afterwards (same positions), then count what its checks flag: errors, conveyor problems, unserved stations. Do the same for the tool plans. The same checker scores both conditions. |
| **Conveyor length and turns** | From the tool's numbers, for every finished plan. |
| **Revisions** | How many times the participant erased or moved something (count on paper; undo count in the tool, from a screen recording). |
| **Usability** | System Usability Scale (SUS, 10 questions, 0–100) after condition B. |
| **One open question** | "What would you need before using this on a real line?" |

Transcribing the paper plans is the important step. It is what makes "fewer errors" a
measurement, not an impression. Note any paper plan that cannot be transcribed at all, for
example equipment off the floor.

## Reporting

- One row per participant: condition order, time A, time B, errors A, errors B, conveyor
  length A and B, SUS.
- Medians and ranges. With 4–6 people, **do not** report p-values or percentages as if they
  generalise. Report what happened: e.g. "4 of 5 paper plans had at least one robot without
  room to swing; 0 of 5 tool plans did."
- Quote the open answers as they were given.
- Say what the test does not show: real shop-floor outcomes, cost, experienced planners using
  their own tools.

## Results

*(Fill in after running the test.)*
