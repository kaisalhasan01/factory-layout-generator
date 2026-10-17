using System.Collections.Generic;
using System.Linq;
using FactoryLayout.Core;
using UnityEngine;

/// <summary>
/// A scripted run of the editor for recordings: <c>-layout x.json -frames dir -demo</c>.
/// It starts from an empty floor and works through the four steps the way a user would. The
/// floor is cut to shape, the equipment goes in one piece at a time, and the conveyor is drawn
/// from IN to OUT along the route the engine finds. Then the line takes 3D form and runs.
/// Every state shown is a real layout, validated and drawn by the same code as in the editor.
/// </summary>
public sealed partial class FactoryApp
{
    bool _demo, _demoDone;
    float _demoT0 = -1;
    Layout _demoTarget;
    List<Cell> _demoVoids, _demoRoute;
    int _demoVoidCount, _demoCompCount, _demoCellCount;

    // Timeline, seconds.
    const float VoidsAt = 0.9f, VoidsFor = 1.6f, EquipAt = 3.1f, EquipEvery = 0.42f, CellsPerSecond = 15f;

    float EquipEnd => EquipAt + EquipEvery * _demoTarget.Components.Count + 0.7f;
    float ConveyorEnd => EquipEnd + 0.35f + _demoRoute.Count / CellsPerSecond + 1.1f;
    float DemoEnd => ConveyorEnd + 2.6f + 4.2f;

    void DemoSetup()
    {
        _demo = true;
        _demoTarget = _layout.Clone();
        var auto = ConveyorRouter.Route(_demoTarget, Options);
        _demoRoute = auto.Success ? auto.Cells : new List<Cell>();
        // Cut the floor from the back wall forward, so it reads as one stroke.
        _demoVoids = _demoTarget.Voids.Where(_demoTarget.InBounds).OrderByDescending(v => v.Y).ThenBy(v => v.X).ToList();

        _layout = new Layout(_demoTarget.Width, _demoTarget.Height, _demoTarget.Name) { CellMm = _demoTarget.CellMm };
        _step = _lastBlueprintStep = Step.Floor;
        _floorTool = FloorTool.Remove;
        _tool = null;
        Recompute();
    }

    void DemoUpdate()
    {
        if (_demoT0 < 0) _demoT0 = Time.time;
        float t = Time.time - _demoT0;
        bool changed = false;

        // Step 1: the floor is cut to shape.
        int voids = Mathf.Clamp(Mathf.FloorToInt((t - VoidsAt) / VoidsFor * _demoVoids.Count), 0, _demoVoids.Count);
        while (_demoVoidCount < voids)
        {
            _layout.Voids.Add(_demoVoids[_demoVoidCount++]);
            _hover = _layout.Voids.Last();
            changed = true;
        }

        // Step 2: equipment, one piece at a time, each shown selected with its properties.
        if (t >= EquipAt && _step == Step.Floor) { _hover = null; SetStep(Step.Equipment); }
        int comps = Mathf.Clamp(Mathf.FloorToInt((t - EquipAt) / EquipEvery) + 1, 0, _demoTarget.Components.Count);
        if (t < EquipAt) comps = 0;
        while (_demoCompCount < comps)
        {
            var c = _demoTarget.Components[_demoCompCount++].Clone();
            _layout.Components.Add(c);
            _tool = c.Kind;
            _selected = c;
            changed = true;
        }

        // Step 3: the conveyor is drawn from IN, cell by cell, and checked as it grows.
        if (t >= EquipEnd && _step == Step.Equipment)
        {
            _selected = null;
            _layout.ConveyorMode = ConveyorMode.Manual;
            _layout.ManualPath.Clear();
            SetStep(Step.Conveyor);
        }
        if (_step == Step.Conveyor)
        {
            int cells = Mathf.Clamp(Mathf.FloorToInt((t - EquipEnd - 0.35f) * CellsPerSecond) + 1, 0, _demoRoute.Count);
            if (cells != _demoCellCount)
            {
                _demoCellCount = cells;
                _layout.ManualPath.Clear();
                _layout.ManualPath.AddRange(_demoRoute.Take(cells));
                changed = true;
            }
        }

        // Step 4: 3D, then a slow turn around the running line.
        if (t >= ConveyorEnd && _step == Step.Conveyor) SetStep(Step.View3D);
        if (_step == Step.View3D && _morph >= 1f) _yaw += 7f * Time.deltaTime;

        if (changed) Recompute();
        if (t >= DemoEnd) _demoDone = true;
    }
}
