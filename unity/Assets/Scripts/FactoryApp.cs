using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FactoryLayout.Core;
using UnityEngine;
using Component = FactoryLayout.Core.Component;

/// <summary>
/// The editor. Four steps — floor, equipment, conveyor, 3D — each checked by the engine as you
/// go. The layout is the only state; everything visible is rebuilt from it.
///
/// Command line (for unattended screenshots):
///   -layout path.json   load a layout             -step floor|equipment|conveyor|3d
///   -view 3d            same as -step 3d          -optimize        tool picks the station order
///   -screenshot f       capture once settled, then quit
///   -frames dir         capture the blueprint-to-3D transition frame by frame, then quit
///   -tour n             show page n of the guided tour (for screenshots)
///   -uiscale f          interface scale multiplier  -yaw a -pitch p -zoom z  3D camera
/// </summary>
public sealed partial class FactoryApp : MonoBehaviour
{
    enum Step { Floor, Equipment, Conveyor, View3D }
    enum FloorTool { Remove, Add }

    const float PanelW = 340, StatusH = 30;
    static readonly ComponentKind[] Tools =
    {
        ComponentKind.Start, ComponentKind.Stop, ComponentKind.Robot, ComponentKind.Drill,
        ComponentKind.XRay, ComponentKind.Pillar, ComponentKind.Machine,
    };
    static readonly string[] StepNames = { "Floor", "Equipment", "Conveyor", "3D view" };
    static readonly string[] Swatches = { "#3D6E9E", "#4F7A5A", "#8A5A9E", "#B5523B", "#C99A2E", "#5A6670", "#2E8C8C", "#D9DEE3" };

    Layout _layout;
    RouteResult _route;
    List<Issue> _issues = new List<Issue>();
    LayoutMetrics _metrics;
    FactoryView _view;
    Camera _cam;

    Step _step = Step.Equipment, _lastBlueprintStep = Step.Equipment;
    FloorTool _floorTool = FloorTool.Remove;
    ComponentKind? _tool = ComponentKind.Robot;   // null = select / move
    Dir _facing = Dir.East;
    Component _selected, _dragging;
    Cell _dragFrom;
    Cell? _hover;
    bool _painting, _drawing, _optimize;
    readonly Stack<string> _undo = new Stack<string>(), _redo = new Stack<string>();

    List<Cell> _focus = new List<Cell>();
    float _focusUntil;

    float _morph, _morphTarget;
    float _yaw = -32, _pitch = 56, _zoom = 1.12f;
    Vector3 _panOffset;

    string _layoutPath, _msg;
    float _msgUntil;
    bool _msgBad, _showLoad;
    Vector2 _contentScroll, _checksScroll, _loadScroll;
    string _wText, _hText, _labelText;
    Component _labelFor;

    float _uiMul = 1f;
    int _tour = -1;

    string _shotPath, _framesDir;
    int _frame, _settle;
    bool Capturing => _shotPath != null || _framesDir != null;

    static string LayoutsDir => Path.GetFullPath(Path.Combine(Application.dataPath, Application.isEditor ? "../../layouts" : "../layouts"));

    // ================================================================== setup

    void Awake()
    {
        Application.targetFrameRate = 60;
        _cam = Camera.main;
        if (_cam == null)
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            _cam = go.AddComponent<Camera>();
        }
        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.backgroundColor = FactoryView.Paper;

        var light = new GameObject("Sun").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(52, -38, 0);
        light.intensity = 1.05f;
        light.shadows = LightShadows.Soft;
        light.shadowStrength = 0.6f;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.52f, 0.55f, 0.6f);
        QualitySettings.shadowDistance = 60;

        _view = new GameObject("View").AddComponent<FactoryView>();

        var args = Environment.GetCommandLineArgs();
        string Arg(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        float FArg(string n, float d) => float.TryParse(Arg(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

        _optimize = args.Contains("-optimize");
        _shotPath = Arg("-screenshot");
        _framesDir = Arg("-frames");
        _yaw = FArg("-yaw", _yaw); _pitch = FArg("-pitch", _pitch); _zoom = FArg("-zoom", _zoom);
        _uiMul = Capturing ? FArg("-uiscale", 1f) : PlayerPrefs.GetFloat("ui.scale", FArg("-uiscale", 1f));
        if (Capturing) Time.captureFramerate = 30;

        _layoutPath = Arg("-layout") ?? Path.Combine(LayoutsDir, "demo-line.json");
        if (!TryLoad(_layoutPath)) { _layout = Fallback(); _layoutPath = null; }

        _step = _layout.OfKind(ComponentKind.Start).Any() && _layout.OfKind(ComponentKind.Stop).Any() ? Step.Conveyor : Step.Floor;
        var stepArg = Arg("-step") ?? (Arg("-view") == "3d" ? "3d" : null);
        if (stepArg != null)
            _step = stepArg == "floor" ? Step.Floor : stepArg == "equipment" ? Step.Equipment : stepArg == "3d" ? Step.View3D : Step.Conveyor;
        if (_step != Step.View3D) _lastBlueprintStep = _step;
        if (_step == Step.Equipment || _step == Step.Floor) _tool = _step == Step.Equipment ? ComponentKind.Robot : (ComponentKind?)null;
        if (Capturing) _tool = null;

        if (_framesDir != null && args.Contains("-demo"))
        {
            Directory.CreateDirectory(_framesDir);
            DemoSetup();
        }
        else if (_framesDir != null)
        {
            // Frames always show the whole transition, from the finished blueprint.
            Directory.CreateDirectory(_framesDir);
            _step = _lastBlueprintStep = Step.Conveyor;
            Recompute();
            SetStep(Step.View3D);
        }
        else
        {
            Recompute();
            if (_step == Step.View3D) { _morph = _morphTarget = 1; _view.ApplyMorph(1); }
        }

        var tourArg = Arg("-tour");
        if (tourArg != null && int.TryParse(tourArg, out int tp)) OpenTour(tp);
        else if (!Capturing && PlayerPrefs.GetInt("tour.done", 0) == 0) OpenTour(0);
    }

    static Layout Fallback()
    {
        var l = new Layout(14, 9, "new-line");
        l.Add(ComponentKind.Start, 0, 4);
        l.Add(ComponentKind.Stop, 13, 4);
        return l;
    }

    bool TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            _layout = LayoutJson.Read(File.ReadAllText(path));
            _layoutPath = path;
            _selected = null;
            return true;
        }
        catch (Exception e) { Say("Could not load " + Path.GetFileName(path) + ": " + e.Message, true); return false; }
    }

    RouteOptions Options => new RouteOptions { OptimizeOrder = _optimize };

    void Recompute()
    {
        _issues = LayoutValidator.Validate(_layout);
        // The conveyor exists from step 3 on: placing equipment should not make it jump around.
        _route = _step >= Step.Conveyor ? ConveyorRouter.Resolve(_layout, Options) : null;
        _metrics = LayoutMetrics.Of(_layout, _route);
        if (_selected != null && !_layout.Components.Contains(_selected)) _selected = null;
        _view.Rebuild(_layout, _route, _issues);
        _view.ApplyMorph(_morph);
        UpdateOverlay();
    }

    void Snapshot()
    {
        _undo.Push(LayoutJson.Write(_layout));
        _redo.Clear();
        if (_undo.Count > 300) { var keep = _undo.Take(150).Reverse().ToList(); _undo.Clear(); foreach (var k in keep) _undo.Push(k); }
    }

    void Undo()
    {
        if (_undo.Count == 0) { Say("Nothing to undo."); return; }
        _redo.Push(LayoutJson.Write(_layout));
        Restore(_undo.Pop());
    }

    void Redo()
    {
        if (_redo.Count == 0) { Say("Nothing to redo."); return; }
        _undo.Push(LayoutJson.Write(_layout));
        Restore(_redo.Pop());
    }

    void Restore(string json)
    {
        var sel = _selected?.Id;
        _layout = LayoutJson.Read(json);
        _selected = sel != null ? _layout.Find(sel) : null;
        Recompute();
    }

    void Say(string m, bool bad = false, float secs = 3.5f) { _msg = m; _msgBad = bad; _msgUntil = Time.unscaledTime + secs; }

    void SetStep(Step s)
    {
        if (s == _step) return;
        _drawing = _painting = false; _dragging = null;
        if (s != Step.Equipment) _selected = null;
        if (s == Step.View3D) { _morphTarget = 1; }
        else { _morphTarget = 0; _lastBlueprintStep = s; }
        if (s == Step.Equipment && _tool == null) _tool = ComponentKind.Robot;
        _step = s;
        Recompute();
    }

    // ================================================================== frame

    float UiScale => Mathf.Clamp(Screen.height / 820f, 1f, 2.2f) * _uiMul;

    void Update()
    {
        if (_demo) DemoUpdate();
        else HandleKeys();
        if (_tour < 0 && !_demo)
        {
            if (_morph < 0.001f && _morphTarget < 0.5f) HandleBlueprintMouse(); else HandleOrbit();
        }

        if (!Mathf.Approximately(_morph, _morphTarget))
        {
            _morph = Mathf.MoveTowards(_morph, _morphTarget, Time.deltaTime / 2.6f);
            _view.ApplyMorph(_morph);
        }
        if (_focus.Count > 0)
        {
            if (Time.unscaledTime > _focusUntil) { _focus.Clear(); UpdateOverlay(); }
            else UpdateOverlay();
        }
        ApplyCamera();
        Capture();
    }

    void HandleKeys()
    {
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (ctrl && (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Plus))) SetUi(_uiMul + 0.1f);
        if (ctrl && (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))) SetUi(_uiMul - 0.1f);
        if (ctrl && (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0))) SetUi(1f);

        if (_tour >= 0)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) CloseTour();
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.Return)) TourGo(_tour + 1);
            if (Input.GetKeyDown(KeyCode.LeftArrow)) TourGo(_tour - 1);
            return;
        }
        if (GUIUtility.keyboardControl != 0) return; // typing in a text field

        if (Input.GetKeyDown(KeyCode.Tab)) SetStep(_step == Step.View3D ? _lastBlueprintStep : Step.View3D);
        if (Input.GetKeyDown(KeyCode.F1)) SetStep(Step.Floor);
        if (Input.GetKeyDown(KeyCode.F2)) SetStep(Step.Equipment);
        if (Input.GetKeyDown(KeyCode.F3)) SetStep(Step.Conveyor);
        if (Input.GetKeyDown(KeyCode.F4)) SetStep(Step.View3D);
        if (ctrl && Input.GetKeyDown(KeyCode.S)) Save();
        if (ctrl && Input.GetKeyDown(KeyCode.Z)) Undo();
        if (ctrl && Input.GetKeyDown(KeyCode.Y)) Redo();
        if (ctrl) return;

        switch (_step)
        {
            case Step.Floor:
                if (Input.GetKeyDown(KeyCode.Alpha1)) _floorTool = FloorTool.Remove;
                if (Input.GetKeyDown(KeyCode.Alpha2)) _floorTool = FloorTool.Add;
                break;
            case Step.Equipment:
                for (int i = 0; i < Tools.Length; i++)
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i)) { _tool = Tools[i]; _selected = null; UpdateOverlay(); }
                if (Input.GetKeyDown(KeyCode.Alpha0)) { _tool = null; UpdateOverlay(); }
                if (Input.GetKeyDown(KeyCode.Escape)) { _selected = null; _tool = null; UpdateOverlay(); }
                if (Input.GetKeyDown(KeyCode.R)) Rotate();
                if ((Input.GetKeyDown(KeyCode.Delete) || Input.GetKeyDown(KeyCode.Backspace)) && _selected != null) Remove(_selected);
                break;
            case Step.Conveyor:
                if (Input.GetKeyDown(KeyCode.O) && _layout.ConveyorMode == ConveyorMode.Auto) { _optimize = !_optimize; Recompute(); }
                break;
        }
    }

    void SetUi(float mul)
    {
        _uiMul = Mathf.Clamp(Mathf.Round(mul * 10) / 10, 0.7f, 2f);
        if (!Capturing) PlayerPrefs.SetFloat("ui.scale", _uiMul);
        Say($"Text size {Mathf.RoundToInt(_uiMul * 100)} %");
    }

    // ================================================================== mouse on the drawing

    Vector2 MouseV => new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / UiScale;

    bool OverUi()
    {
        var m = MouseV;
        float vw = Screen.width / UiScale, vh = Screen.height / UiScale;
        if (PanelRect(vh).Contains(m) || new Rect(0, vh - StatusH, vw, StatusH).Contains(m)) return true;
        if (_showLoad && LoadRect(vw, vh).Contains(m)) return true;
        return GUIUtility.hotControl != 0;
    }

    Cell? MouseCell(bool clampToRect = false)
    {
        var ray = _cam.ScreenPointToRay(Input.mousePosition);
        if (Mathf.Abs(ray.direction.y) < 1e-5f) return null;
        float t = -ray.origin.y / ray.direction.y;
        if (t < 0) return null;
        var c = _view.CellAt(ray.origin + ray.direction * t);
        return _layout.InBounds(c) ? c : (Cell?)null;
    }

    bool BodyFits(Component c, Component ignore) =>
        c.BodyCells().All(b => _layout.IsFloor(b) && _layout.Components.All(o => o == ignore || o == c || !o.BodyCells().Contains(b)));

    void HandleBlueprintMouse()
    {
        bool overUi = OverUi();
        if (!overUi && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1))) GUIUtility.keyboardControl = 0;
        var cell = overUi ? null : MouseCell();
        if (!Nullable.Equals(cell, _hover)) { _hover = cell; UpdateOverlay(); }
        if (Input.GetMouseButtonUp(0)) { _painting = _drawing = false; if (_dragging != null) EndDrag(); }
        if (cell == null) return;

        switch (_step)
        {
            case Step.Floor: FloorMouse(cell.Value); break;
            case Step.Equipment: EquipmentMouse(cell.Value); break;
            case Step.Conveyor: ConveyorMouse(cell.Value); break;
        }
    }

    // ---- step 1: floor

    void FloorMouse(Cell cell)
    {
        if (Input.GetMouseButtonDown(0)) { Snapshot(); _painting = true; }
        if (Input.GetMouseButtonDown(1)) { Snapshot(); _painting = true; }
        if (!_painting) return;
        bool remove = Input.GetMouseButton(1) ? _floorTool == FloorTool.Add : _floorTool == FloorTool.Remove;
        if (!Input.GetMouseButton(0) && !Input.GetMouseButton(1)) { _painting = false; return; }
        if (remove)
        {
            if (_layout.Voids.Contains(cell)) return;
            var on = _layout.BodyAt(cell);
            if (on != null) { Say($"{on.Id} stands there: move it first.", true); return; }
            _layout.Voids.Add(cell);
            if (_layout.ManualPath.Contains(cell)) Say("The drawn conveyor ran over that cell; it is now flagged.", true);
        }
        else if (!_layout.Voids.Remove(cell)) return;
        Recompute();
    }

    // ---- step 2: equipment

    void EquipmentMouse(Cell cell)
    {
        var at = _layout.BodyAt(cell);
        if (Input.GetMouseButtonDown(0))
        {
            if (at != null) { _selected = at; _dragging = at; _dragFrom = at.Origin; _dragGrab = cell; Snapshot(); }
            else if (_tool != null) Place(_tool.Value, cell);
            else _selected = null;
            UpdateOverlay();
        }
        if (Input.GetMouseButton(0) && _dragging != null)
        {
            // Keep the grab point: move by the cursor's offset from where the drag began.
            var target = new Cell(_dragging.Origin.X + (cell.X - _dragGrab.X), _dragging.Origin.Y + (cell.Y - _dragGrab.Y));
            if (target != _dragging.Origin)
            {
                var old = _dragging.Origin;
                _dragging.Origin = target;
                if (BodyFits(_dragging, _dragging)) { _dragGrab = cell; Recompute(); }
                else _dragging.Origin = old;
            }
        }
        if (Input.GetMouseButtonDown(1) && at != null) Remove(at);
    }

    Cell _dragGrab;

    void EndDrag()
    {
        if (_dragging.Origin == _dragFrom && _undo.Count > 0) _undo.Pop(); // a click, not a move
        _dragging = null;
    }

    void Place(ComponentKind kind, Cell cell)
    {
        if (kind == ComponentKind.Start || kind == ComponentKind.Stop)
        {
            var existing = _layout.OfKind(kind).FirstOrDefault();
            if (existing != null)
            {
                var old = existing.Origin;
                existing.Origin = cell;
                bool fits = BodyFits(existing, existing);
                existing.Origin = old;
                if (!fits) { Say("That cell is taken.", true); return; }
                Snapshot();
                existing.Origin = cell;
                _selected = existing;
                Recompute();
                Say($"{existing.Id} moved — there is only one {existing.Spec.Name}.");
                return;
            }
        }
        var c = new Component(_layout.NextId(kind), kind, cell, _facing);
        if (!BodyFits(c, null)) { Say(c.Length > 1 || c.Width > 1 ? "Does not fit there: rotate with R or pick a free spot." : "That cell is taken.", true); return; }
        Snapshot();
        _layout.Components.Add(c);
        _selected = c;
        Recompute();
    }

    void Rotate()
    {
        if (_selected != null)
        {
            Snapshot();
            var before = _selected.Origin; var bf = _selected.Facing;
            _selected.RotateInPlace();
            if (!BodyFits(_selected, _selected)) { _selected.Origin = before; _selected.Facing = bf; _undo.Pop(); Say("No room to rotate it here.", true); return; }
            Recompute();
        }
        else { _facing = _facing.RotateCw(); UpdateOverlay(); }
    }

    void Remove(Component c)
    {
        Snapshot();
        _layout.Components.Remove(c);
        if (_selected == c) _selected = null;
        Recompute();
    }

    // ---- step 3: conveyor

    void ConveyorMouse(Cell cell)
    {
        if (_layout.ConveyorMode != ConveyorMode.Manual)
        {
            if (Input.GetMouseButtonDown(0)) Say("The tool is routing the belt. Choose \"Draw it myself\" to draw it.");
            return;
        }
        var path = _layout.ManualPath;
        var start = _layout.OfKind(ComponentKind.Start).FirstOrDefault();
        if (start == null) { if (Input.GetMouseButtonDown(0)) Say("Place IN (Start) first, in step 2.", true); return; }

        if (Input.GetMouseButtonDown(0))
        {
            int at = path.IndexOf(cell);
            if (path.Count == 0)
            {
                if (cell != start.Origin) { Say("Start drawing on IN: press there and drag.", true); return; }
                Snapshot(); path.Add(cell); _drawing = true;
            }
            else if (at >= 0)
            {
                Snapshot(); path.RemoveRange(at + 1, path.Count - at - 1); _drawing = true;
            }
            else if (Cell.Manhattan(path[path.Count - 1], cell) == 1)
            {
                Snapshot(); _drawing = true; Extend(cell);
            }
            else { Say("Carry on from the end of the belt (the yellow dot), or press on the belt to redraw from there.", true); return; }
            Recompute();
        }
        else if (Input.GetMouseButton(0) && _drawing && path.Count > 0 && path[path.Count - 1] != cell)
        {
            Extend(cell);
            Recompute();
        }
        if (Input.GetMouseButtonDown(1))
        {
            int at = path.IndexOf(cell);
            if (at >= 0) { Snapshot(); path.RemoveRange(at, path.Count - at); Recompute(); }
        }
    }

    /// <summary>Grows the drawn conveyor towards the cursor one cell at a time; moving back over it un-draws.</summary>
    void Extend(Cell target)
    {
        var path = _layout.ManualPath;
        var stop = _layout.OfKind(ComponentKind.Stop).FirstOrDefault();
        for (int guard = 0; guard < 200 && path.Count > 0; guard++)
        {
            var last = path[path.Count - 1];
            if (last == target) return;
            if (stop != null && last == stop.Origin) return;
            int dx = target.X - last.X, dy = target.Y - last.Y;
            // Prefer carrying on in the current direction, so fast drags do not zig-zag.
            Dir step;
            if (dx != 0 && dy != 0 && path.Count >= 2)
            {
                var cur = Dirs.Between(path[path.Count - 2], last);
                bool curOk = (cur.Dx() != 0 && Math.Sign(dx) == cur.Dx()) || (cur.Dy() != 0 && Math.Sign(dy) == cur.Dy());
                step = curOk ? cur : (Math.Abs(dx) >= Math.Abs(dy) ? (dx > 0 ? Dir.East : Dir.West) : (dy > 0 ? Dir.North : Dir.South));
            }
            else step = Math.Abs(dx) >= Math.Abs(dy) ? (dx > 0 ? Dir.East : Dir.West) : (dy > 0 ? Dir.North : Dir.South);
            var next = last.Step(step);

            if (path.Count >= 2 && next == path[path.Count - 2]) { path.RemoveAt(path.Count - 1); continue; }
            if (path.Contains(next)) { Say("Conveyors cannot cross themselves.", true); return; }
            if (!_layout.IsFloor(next)) { Say("That is not floor.", true); return; }
            var body = _layout.BodyAt(next);
            if (body != null && body.Kind != ComponentKind.XRay && body.Kind != ComponentKind.Stop)
            {
                Say($"The belt cannot run through {body.Id} ({body.DisplayName}).", true);
                return;
            }
            path.Add(next);
        }
    }

    void FinishAutomatically()
    {
        var r = ConveyorRouter.Complete(_layout, _layout.ManualPath, Options);
        if (!r.Success) { Say(r.Failure, true, 6); return; }
        Snapshot();
        _layout.ManualPath.Clear();
        _layout.ManualPath.AddRange(r.Cells);
        Recompute();
        Say("Finished from your drawing. Edit it by pressing on the belt and dragging.");
    }

    void SetMode(ConveyorMode mode)
    {
        if (_layout.ConveyorMode == mode) return;
        Snapshot();
        _layout.ConveyorMode = mode;
        Recompute();
    }

    void EditAutoRoute()
    {
        var r = ConveyorRouter.Route(_layout, Options);
        if (!r.Success) { Say("There is no automatic route to start from: " + r.Failure, true, 6); return; }
        Snapshot();
        _layout.ConveyorMode = ConveyorMode.Manual;
        _layout.ManualPath.Clear();
        _layout.ManualPath.AddRange(r.Cells);
        Recompute();
        Say("The route is yours now: press on the belt and drag to redraw from there.");
    }

    // ---- overlay

    void UpdateOverlay()
    {
        var st = new OverlayState { Selected = _step == Step.Equipment ? _selected : null, Focus = _focus };
        st.Pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 7f);
        if (_hover != null && _morphTarget < 0.5f)
        {
            if (_step == Step.Equipment && _tool != null && _dragging == null && _layout.BodyAt(_hover.Value) == null)
            {
                var ghost = new Component("?", _tool.Value, _hover.Value, _facing);
                bool valid = BodyFits(ghost, null);
                if (valid)
                {
                    var trial = _layout.Clone();
                    trial.Components.Add(ghost.Clone());
                    valid = !LayoutValidator.Validate(trial).Any(i => i.Severity == Severity.Error && i.ComponentIds.Contains("?")
                                                                     && !i.Code.EndsWith("_COUNT"));
                }
                st.Ghost = ghost; st.GhostValid = valid;
            }
            else if (_step == Step.Floor)
            {
                st.HoverCell = _hover;
                st.HoverColour = _floorTool == FloorTool.Remove ? FactoryView.Bad : FactoryView.Ink;
            }
            else if (_step == Step.Conveyor && _layout.ConveyorMode == ConveyorMode.Manual)
            {
                st.HoverCell = _hover; st.HoverColour = FactoryView.Warn;
            }
        }
        if (_step == Step.Conveyor && _layout.ConveyorMode == ConveyorMode.Manual)
        {
            var stop = _layout.OfKind(ComponentKind.Stop).FirstOrDefault();
            if (_layout.ManualPath.Count > 0 && (stop == null || _layout.ManualPath[_layout.ManualPath.Count - 1] != stop.Origin))
                st.PathEnd = _layout.ManualPath[_layout.ManualPath.Count - 1];
            else if (_layout.ManualPath.Count == 0)
                st.PathEnd = _layout.OfKind(ComponentKind.Start).FirstOrDefault()?.Origin;
        }
        _view.SetOverlay(st);
    }

    void FocusIssue(Issue i)
    {
        var cells = i.Cells.ToList();
        foreach (var id in i.ComponentIds) { var c = _layout.Find(id); if (c != null && cells.Count == 0) cells.AddRange(c.BodyCells()); }
        _focus = cells;
        _focusUntil = Time.unscaledTime + 3f;
        if (_step == Step.View3D) SetStep(_lastBlueprintStep);
        var first = i.ComponentIds.Select(_layout.Find).FirstOrDefault(c => c != null);
        if (first != null && _step == Step.Equipment) _selected = first;
        UpdateOverlay();
    }

    // ================================================================== 3D camera

    void HandleOrbit()
    {
        if (OverUi() && !Input.GetMouseButton(1)) return;
        if (Input.GetMouseButton(1) && !Input.GetKey(KeyCode.LeftShift))
        {
            _yaw += Input.GetAxis("Mouse X") * 3.2f;
            _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * 2.4f, 12, 85);
        }
        if (Input.GetMouseButton(2) || (Input.GetMouseButton(1) && Input.GetKey(KeyCode.LeftShift)))
        {
            var rot = Quaternion.Euler(0, _yaw, 0);
            _panOffset -= rot * new Vector3(Input.GetAxis("Mouse X"), 0, Input.GetAxis("Mouse Y")) * 0.12f * _zoom * Fit();
        }
        float wheel = Input.mouseScrollDelta.y;
        if (Mathf.Abs(wheel) > 0.01f) _zoom = Mathf.Clamp(_zoom * Mathf.Pow(0.9f, wheel), 0.25f, 3f);
    }

    float PanelPx => (PanelW + 20) * UiScale;
    float StatusPx => StatusH * UiScale;

    float Fit()
    {
        float s = _layout.CellMm / 1000f, fw = _layout.Width * s, fh = _layout.Height * s;
        float availW = Mathf.Max(200, Screen.width - PanelPx), availH = Mathf.Max(200, Screen.height - StatusPx);
        float avail = availW / availH;
        float half = Mathf.Max(fh / 2 + 0.6f, (fw / 2 + 0.5f) / avail) * 1.06f;
        return half * Screen.height / availH;
    }

    /// <summary>
    /// A dolly zoom from blueprint to 3D: top-down through a 2° lens from far away (practically
    /// orthographic), then tilting down while the lens widens and the camera closes in, keeping
    /// the floor the same size on screen.
    /// </summary>
    void ApplyCamera()
    {
        float e = Mathf.Clamp01(_morph / 0.75f);
        e = e * e * (3 - 2 * e);
        float s = _layout.CellMm / 1000f;
        var centre = new Vector3(_layout.Width * s / 2, 0, _layout.Height * s / 2);
        float half = Fit();

        float fov = Mathf.Exp(Mathf.Lerp(Mathf.Log(2f), Mathf.Log(42f), e));
        float dolly = half / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
        float dist = dolly * Mathf.Lerp(1f, 1.05f * _zoom, e);
        var rot = Quaternion.Euler(Mathf.Lerp(90f, _pitch, e), Mathf.LerpAngle(0f, _yaw, e), 0);
        var pivot = centre + _panOffset * e;

        _cam.transform.rotation = rot;
        _cam.transform.position = pivot - rot * Vector3.forward * dist;
        _cam.fieldOfView = fov;
        _cam.nearClipPlane = Mathf.Max(0.05f, dist * 0.02f);
        _cam.farClipPlane = dist + 200f;

        // Centre the floor in the area beside the panel and above the status bar.
        _cam.ResetProjectionMatrix();
        var p = _cam.projectionMatrix;
        p.m02 = -(PanelPx / Screen.width);
        p.m12 = -(StatusPx / Screen.height);
        _cam.projectionMatrix = p;

        _cam.backgroundColor = Color.Lerp(FactoryView.Paper, new Color(0.12f, 0.14f, 0.17f), Mathf.Clamp01((_morph - 0.2f) / 0.5f));
    }

    // ================================================================== capture

    void Capture()
    {
        if (_demo)
        {
            if (_frame >= 4 && _frame % 2 == 0)
                ScreenCapture.CaptureScreenshot(Path.Combine(_framesDir, $"frame_{_frame / 2:D4}.png"));
            _frame++;
            if (_demoDone) Application.Quit();
            return;
        }
        if (_framesDir != null)
        {
            if (_frame >= 6 && _frame % 2 == 0)
                ScreenCapture.CaptureScreenshot(Path.Combine(_framesDir, $"frame_{_frame / 2:D4}.png"));
            _frame++;
            if (_morph >= 1f) _settle++;
            if (_settle > 90) Application.Quit();
            return;
        }
        if (_shotPath == null) return;
        _settle++;
        if (_settle == 75) ScreenCapture.CaptureScreenshot(_shotPath);
        if (_settle == 80) Application.Quit();
    }

    // ================================================================== saving

    void Save()
    {
        try
        {
            Directory.CreateDirectory(LayoutsDir);
            var path = _layoutPath ?? Path.Combine(LayoutsDir, _layout.Name + ".json");
            var route = ConveyorRouter.Resolve(_layout, Options);
            File.WriteAllText(path, LayoutJson.Write(_layout, route));
            File.WriteAllText(Path.ChangeExtension(path, ".svg"), BlueprintSvg.Render(_layout, route, _issues));
            _layoutPath = path;
            Say("Saved " + Path.GetFileName(path) + " and a blueprint .svg next to it.", false, 5);
        }
        catch (Exception e) { Say("Save failed: " + e.Message, true, 6); }
    }

    // ================================================================== GUI: styles

    GUIStyle _toolBtn, _toolOn, _toggle, _panel, _card, _btn, _btnOn, _tab, _tabOn, _h1, _h2, _body, _small, _err, _warn, _ok, _field, _issueBtn, _status, _toast, _toastBad, _tourCard, _label;
    Texture2D _white;
    static readonly Color Accent = new Color32(0x3A, 0x86, 0xFF, 255);
    static readonly Color TextMain = new Color(0.92f, 0.95f, 1f);
    static readonly Color TextMuted = new Color(0.62f, 0.71f, 0.82f);

    void Styles()
    {
        if (_h1 != null) return;
        _white = Tex(Color.white, 0);
        GUIStyle Box(Color c, int r = 6) => new GUIStyle { normal = { background = Tex(c, r) }, border = new RectOffset(r + 1, r + 1, r + 1, r + 1), padding = new RectOffset(12, 12, 10, 10) };
        _panel = Box(new Color(0.055f, 0.10f, 0.17f, 0.96f), 8);
        _card = Box(new Color(0.09f, 0.16f, 0.25f, 1f), 6);
        _card.margin = new RectOffset(0, 0, 4, 6);
        _tourCard = Box(new Color(0.07f, 0.13f, 0.21f, 0.99f), 10);
        _tourCard.padding = new RectOffset(20, 20, 16, 16);

        GUIStyle Btn(Color n, Color h, Color a, int size = 14)
        {
            var s = new GUIStyle
            {
                normal = { background = Tex(n, 5), textColor = TextMain },
                hover = { background = Tex(h, 5), textColor = Color.white },
                active = { background = Tex(a, 5), textColor = Color.white },
                border = new RectOffset(6, 6, 6, 6), padding = new RectOffset(10, 10, 7, 7), margin = new RectOffset(2, 2, 2, 2),
                alignment = TextAnchor.MiddleCenter, fontSize = size, wordWrap = false,
            };
            return s;
        }
        _btn = Btn(new Color(0.13f, 0.22f, 0.34f), new Color(0.18f, 0.30f, 0.45f), new Color(0.23f, 0.43f, 0.68f));
        _btnOn = Btn(Accent, Color.Lerp(Accent, Color.white, 0.15f), Accent);
        _btnOn.fontStyle = FontStyle.Bold;
        _tab = Btn(new Color(0.10f, 0.17f, 0.27f), new Color(0.16f, 0.27f, 0.40f), new Color(0.23f, 0.43f, 0.68f), 12);
        _tab.padding = new RectOffset(2, 2, 4, 4);
        _tabOn = Btn(Accent, Accent, Accent, 12);
        _tabOn.fontStyle = FontStyle.Bold; _tabOn.padding = new RectOffset(2, 2, 4, 4);
        _toolBtn = new GUIStyle(_btn) { alignment = TextAnchor.MiddleLeft };
        _toolOn = new GUIStyle(_btnOn) { alignment = TextAnchor.MiddleLeft };

        _h1 = new GUIStyle { fontSize = 20, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        _h2 = new GUIStyle { fontSize = 15, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.74f, 0.85f, 1f) }, margin = new RectOffset(0, 0, 10, 4) };
        _body = new GUIStyle { fontSize = 14, wordWrap = true, richText = true, normal = { textColor = TextMain }, margin = new RectOffset(0, 0, 2, 4) };
        _small = new GUIStyle(_body) { fontSize = 12, normal = { textColor = TextMuted } };
        _err = new GUIStyle(_body) { normal = { textColor = FactoryView.Bad } };
        _warn = new GUIStyle(_body) { normal = { textColor = FactoryView.Warn } };
        _ok = new GUIStyle(_body) { normal = { textColor = new Color(0.55f, 0.9f, 0.6f) } };
        _label = new GUIStyle(_body) { alignment = TextAnchor.MiddleLeft, wordWrap = false };
        _toggle = new GUIStyle(GUI.skin.toggle) { fontSize = 14, normal = { textColor = TextMain }, onNormal = { textColor = Color.white },
            hover = { textColor = Color.white }, onHover = { textColor = Color.white }, margin = new RectOffset(2, 2, 6, 6) };
        _field = new GUIStyle(GUI.skin.textField) { fontSize = 14, normal = { background = Tex(new Color(0.04f, 0.08f, 0.13f), 4), textColor = Color.white },
            focused = { background = Tex(new Color(0.04f, 0.08f, 0.13f), 4), textColor = Color.white }, border = new RectOffset(5, 5, 5, 5), padding = new RectOffset(8, 8, 6, 6) };
        _issueBtn = new GUIStyle(_body) { hover = { background = Tex(new Color(1, 1, 1, 0.06f), 4) }, padding = new RectOffset(6, 6, 4, 4), border = new RectOffset(5, 5, 5, 5) };
        _status = new GUIStyle { fontSize = 13, normal = { background = Tex(new Color(0.04f, 0.08f, 0.13f, 0.95f), 0), textColor = TextMuted }, padding = new RectOffset(14, 14, 7, 7), richText = true };
        _toast = Box(new Color(0.12f, 0.24f, 0.40f, 0.97f), 8);
        _toast.normal.textColor = Color.white; _toast.fontSize = 15; _toast.alignment = TextAnchor.MiddleCenter; _toast.wordWrap = true;
        _toastBad = new GUIStyle(_toast) { normal = { background = Tex(new Color(0.45f, 0.14f, 0.14f, 0.97f), 8), textColor = Color.white } };
    }

    /// <summary>A rounded-corner fill texture for 9-sliced backgrounds.</summary>
    static Texture2D Tex(Color c, int radius)
    {
        int n = Math.Max(1, radius * 2 + 2);
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float a = 1;
                if (radius > 0)
                {
                    float cx = Mathf.Clamp(x + 0.5f, radius, n - radius), cy = Mathf.Clamp(y + 0.5f, radius, n - radius);
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    a = Mathf.Clamp01(radius - d + 0.5f);
                }
                px[y * n + x] = new Color(c.r, c.g, c.b, c.a * a);
            }
        t.SetPixels(px);
        t.Apply();
        return t;
    }

    void Swatch(Rect r, Color c) { var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, _white); GUI.color = old; }

    bool Button(string text, bool on = false, params GUILayoutOption[] o) => GUILayout.Button(text, on ? _btnOn : _btn, o);

    // ================================================================== GUI: layout

    static Rect PanelRect(float vh) => new Rect(10, 10, PanelW, vh - 20 - StatusH);
    static Rect LoadRect(float vw, float vh) => new Rect(PanelW + 30, 70, 360, Mathf.Min(460, vh - 140));

    void OnGUI()
    {
        Styles();
        float sc = UiScale;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(sc, sc, 1));
        float vw = Screen.width / sc, vh = Screen.height / sc;

        Labels(sc);
        if (_framesDir != null && !_demo) return;

        var panel = PanelRect(vh);
        GUI.Box(panel, GUIContent.none, _panel);

        // Header.
        GUI.Label(new Rect(panel.x + 14, panel.y + 12, 200, 28), "Factory Layout", _h1);
        if (GUI.Button(new Rect(panel.xMax - 132, panel.y + 12, 38, 28), "A−", _btn)) SetUi(_uiMul - 0.1f);
        if (GUI.Button(new Rect(panel.xMax - 90, panel.y + 12, 38, 28), "A+", _btn)) SetUi(_uiMul + 0.1f);
        if (GUI.Button(new Rect(panel.xMax - 48, panel.y + 12, 34, 28), "?", _btn)) OpenTour(0);
        GUI.Label(new Rect(panel.x + 14, panel.y + 42, panel.width - 28, 18),
            _layout.Name + (_layoutPath != null ? "  ·  " + Path.GetFileName(_layoutPath) : "  ·  not saved"), _small);

        // Step tabs.
        var tabsRect = TabsRect(panel);
        float tw = (tabsRect.width - 6) / 4;
        for (int i = 0; i < 4; i++)
        {
            var r = new Rect(tabsRect.x + i * (tw + 2), tabsRect.y, tw, tabsRect.height);
            int errs = StepProblems((Step)i);
            string label = $"{i + 1}\n{StepNames[i]}" + (errs > 0 ? $"  ({errs})" : "");
            if (GUI.Button(r, label, _step == (Step)i ? _tabOn : _tab)) SetStep((Step)i);
        }

        // Checks and footer, fixed at the bottom.
        var footer = new Rect(panel.x + 12, panel.yMax - 46, panel.width - 24, 36);
        var checks = ChecksRect(panel);
        DrawChecks(checks);
        GUILayout.BeginArea(footer);
        GUILayout.BeginHorizontal();
        if (Button("Save")) Save();
        if (Button("Load…", _showLoad)) _showLoad = !_showLoad;
        if (Button("Undo")) Undo();
        if (Button("Redo")) Redo();
        GUILayout.EndHorizontal();
        GUILayout.EndArea();

        // Step content, scrolling.
        var content = new Rect(panel.x + 12, tabsRect.yMax + 8, panel.width - 24, checks.y - tabsRect.yMax - 14);
        GUILayout.BeginArea(content);
        _contentScroll = GUILayout.BeginScrollView(_contentScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
        switch (_step)
        {
            case Step.Floor: FloorPanel(); break;
            case Step.Equipment: EquipmentPanel(); break;
            case Step.Conveyor: ConveyorPanel(); break;
            case Step.View3D: ViewPanel(); break;
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();

        StatusBar(vw, vh);
        if (_showLoad) LoadPopup(vw, vh);

        if (_msg != null && Time.unscaledTime < _msgUntil)
        {
            float w = Mathf.Min(620, vw - PanelW - 80);
            var style = _msgBad ? _toastBad : _toast;
            float h = style.CalcHeight(new GUIContent(_msg), w) + 4;
            GUI.Label(new Rect(PanelW + 20 + (vw - PanelW - 20 - w) / 2, vh - StatusH - h - 16, w, h), _msg, style);
        }

        if (_tour >= 0) Tour(vw, vh);
    }

    static Rect TabsRect(Rect panel) => new Rect(panel.x + 12, panel.y + 66, panel.width - 24, 50);
    Rect ChecksRect(Rect panel)
    {
        float h = _issues.Count == 0 ? 64 : Mathf.Clamp(36 + 46 * _issues.Count, 110, panel.height * 0.32f);
        return new Rect(panel.x + 12, panel.yMax - 54 - h, panel.width - 24, h);
    }

    int StepProblems(Step s)
    {
        switch (s)
        {
            case Step.Floor: return _issues.Count(i => i.Severity == Severity.Error && i.Code == "E_FLOOR");
            case Step.Equipment: return _issues.Count(i => i.Severity == Severity.Error);
            case Step.Conveyor: return _route != null && !_route.Success ? 1 : 0;
            default: return 0;
        }
    }

    // ---- step panels

    void FloorPanel()
    {
        GUILayout.Label("Floor", _h2);
        GUILayout.Label("Set the size of the hall, then cut out what is not floor: columns, shafts, offices, an L-shaped corner.", _small);
        float cm = _layout.CellMm / 1000f;
        SizeRow("Width", _layout.Width, v => _layout.Width = v, ref _wText);
        SizeRow("Depth", _layout.Height, v => _layout.Height = v, ref _hText);
        GUILayout.Label($"Grid {_layout.CellMm} mm. Floor area {_layout.FloorCellCount * cm * cm:0.##} m² ({_layout.FloorCellCount} tiles).", _small);

        GUILayout.Label("Edit the floor", _h2);
        GUILayout.BeginHorizontal();
        if (Button("1  Remove floor", _floorTool == FloorTool.Remove)) _floorTool = FloorTool.Remove;
        if (Button("2  Add floor", _floorTool == FloorTool.Add)) _floorTool = FloorTool.Add;
        GUILayout.EndHorizontal();
        GUILayout.Label("Click or drag over tiles. Right-drag does the opposite.", _small);
        if (_layout.Voids.Count > 0 && Button("Reset to a full rectangle")) { Snapshot(); _layout.Voids.Clear(); Recompute(); }

        GUILayout.Space(10);
        if (Button("Next: place equipment  →")) SetStep(Step.Equipment);
    }

    void SizeRow(string label, int cells, Action<int> set, ref string text)
    {
        float cm = _layout.CellMm / 1000f;
        GUILayout.BeginHorizontal(_card);
        GUILayout.Label(label, _label, GUILayout.Width(52), GUILayout.Height(30));
        if (Button("−", false, GUILayout.Width(34), GUILayout.Height(30))) Resize(cells - 1, set);
        string ctrl = label + "Field";
        GUI.SetNextControlName(ctrl);
        bool focused = GUI.GetNameOfFocusedControl() == ctrl;
        if (!focused) text = (cells * cm).ToString("0.0", CultureInfo.InvariantCulture);
        text = GUILayout.TextField(text, _field, GUILayout.Width(64), GUILayout.Height(30));
        GUILayout.Label("m", _label, GUILayout.Width(18), GUILayout.Height(30));
        if (Button("+", false, GUILayout.Width(34), GUILayout.Height(30))) Resize(cells + 1, set);
        GUILayout.EndHorizontal();
        if (focused && Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
        {
            if (float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
                Resize(Mathf.RoundToInt(m / cm), set);
            GUI.FocusControl(null);
            Event.current.Use();
        }
    }

    void Resize(int cells, Action<int> set)
    {
        cells = Mathf.Clamp(cells, 4, 60);
        Snapshot(); set(cells); Recompute();
    }

    void EquipmentPanel()
    {
        GUILayout.Label("Equipment", _h2);
        for (int i = 0; i <= Tools.Length; i += 2)
        {
            GUILayout.BeginHorizontal();
            for (int j = i; j < i + 2; j++)
            {
                bool select = j == Tools.Length;
                if (j > Tools.Length) break;
                string name = select ? "0  Select / move" : $"{j + 1}  {Catalog.Get(Tools[j]).Name}";
                bool on = select ? _tool == null : _tool == Tools[j];
                if (GUILayout.Button(name, on ? _toolOn : _toolBtn, GUILayout.Height(32), GUILayout.Width(150)))
                {
                    _tool = select ? (ComponentKind?)null : Tools[j];
                    if (!select) _selected = null;
                    UpdateOverlay();
                }
                if (!select) Swatch(new Rect(GUILayoutUtility.GetLastRect().xMax - 16, GUILayoutUtility.GetLastRect().y + 11, 10, 10), ToolColour(Tools[j]));
            }
            GUILayout.EndHorizontal();
        }

        if (_selected != null) SelectedCard(_selected);
        else if (_tool != null)
        {
            GUILayout.BeginVertical(_card);
            GUILayout.Label(Catalog.Get(_tool.Value).Name, _label);
            GUILayout.Label(Catalog.Get(_tool.Value).Description, _small);
            GUILayout.Label($"Facing {_facing}  ·  R rotates before placing", _small);
            GUILayout.EndVertical();
        }
        else GUILayout.Label("Click equipment to select it. Drag to move, R rotates, Delete removes.", _small);

        OrderList();
        GUILayout.Space(10);
        if (Button("Next: lay the conveyor  →")) SetStep(Step.Conveyor);
    }

    static Color ToolColour(ComponentKind k)
    {
        switch (k)
        {
            case ComponentKind.Start: return Models.Go;
            case ComponentKind.Stop: return Models.StopRed;
            case ComponentKind.Robot: return Models.RobotOrange;
            case ComponentKind.Drill: return Models.MachineBlue;
            case ComponentKind.XRay: return Models.Panel;
            case ComponentKind.Pillar: return Models.Concrete;
            default: return new Color32(0x3D, 0x6E, 0x9E, 255);
        }
    }

    void SelectedCard(Component c)
    {
        GUILayout.BeginVertical(_card);
        GUILayout.Label($"{c.Id}  ·  {c.Spec.Name}", _label);
        GUILayout.Label($"At {RouteChecker.Name(c.Origin)}, facing {c.Facing}", _small);

        GUILayout.BeginHorizontal();
        GUILayout.Label("Name", _label, GUILayout.Width(52), GUILayout.Height(30));
        if (_labelFor != c) { _labelFor = c; _labelText = c.Label ?? ""; }
        var t = GUILayout.TextField(_labelText, 40, _field, GUILayout.Height(30));
        if (t != _labelText) { _labelText = t; c.Label = string.IsNullOrWhiteSpace(t) ? null : t; }
        GUILayout.EndHorizontal();

        if (c.Spec.Resizable)
        {
            GUILayout.Label($"Size: {c.Length} × {c.Width} tiles  ({c.Length * _layout.CellMm / 1000f:0.#} × {c.Width * _layout.CellMm / 1000f:0.#} m)", _small);
            DimRow("Depth", c.Length, v => c.Length = v, c);
            DimRow("Width", c.Width, v => c.Width = v, c);
            GUILayout.BeginHorizontal();
            foreach (var hex in Swatches)
            {
                ColorUtility.TryParseHtmlString(hex, out var col);
                var r = GUILayoutUtility.GetRect(28, 28, GUILayout.Width(28), GUILayout.Height(28));
                Swatch(new Rect(r.x + 2, r.y + 2, 24, 24), col);
                if (string.Equals(c.Colour, hex, StringComparison.OrdinalIgnoreCase)) Swatch(new Rect(r.x + 2, r.yMax - 4, 24, 3), Color.white);
                if (GUI.Button(r, GUIContent.none, GUIStyle.none)) { Snapshot(); c.Colour = hex; Recompute(); }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("The yellow side is where the conveyor passes and products are loaded.", _small);
        }

        GUILayout.BeginHorizontal();
        if (Button("Rotate (R)")) Rotate();
        if (Button("Delete")) Remove(c);
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    void DimRow(string label, int value, Action<int> set, Component c)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _label, GUILayout.Width(52), GUILayout.Height(28));
        foreach (var d in new[] { -1, 1 })
        {
            if (!Button(d < 0 ? "−" : "+", false, GUILayout.Width(36), GUILayout.Height(28))) continue;
            Snapshot();
            set(value + d);
            if (!BodyFits(c, c)) { set(value); _undo.Pop(); Say("No room to grow it that way.", true); }
            else Recompute();
        }
        GUILayout.Label($"{value}", _label, GUILayout.Height(28));
        GUILayout.EndHorizontal();
    }

    void OrderList()
    {
        GUILayout.Label("Process order", _h2);
        var stations = _layout.Stations.ToList();
        if (stations.Count == 0) { GUILayout.Label("No stations yet. Robots, drills, X-rays and machines are visited in this order.", _small); return; }
        for (int i = 0; i < stations.Count; i++)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{i + 1}.  {stations[i].Id}  {stations[i].DisplayName}", _label, GUILayout.Height(26));
            if (Button("↑", false, GUILayout.Width(30), GUILayout.Height(26)) && i > 0) SwapStations(stations[i], stations[i - 1]);
            if (Button("↓", false, GUILayout.Width(30), GUILayout.Height(26)) && i < stations.Count - 1) SwapStations(stations[i], stations[i + 1]);
            GUILayout.EndHorizontal();
        }
    }

    void ConveyorPanel()
    {
        GUILayout.Label("Conveyor", _h2);
        GUILayout.BeginHorizontal();
        if (Button("Route automatically", _layout.ConveyorMode == ConveyorMode.Auto, GUILayout.Height(34))) SetMode(ConveyorMode.Auto);
        if (Button("Draw it myself", _layout.ConveyorMode == ConveyorMode.Manual, GUILayout.Height(34))) SetMode(ConveyorMode.Manual);
        GUILayout.EndHorizontal();

        if (_layout.ConveyorMode == ConveyorMode.Auto)
        {
            GUILayout.Label("The tool lays the belt from IN through every station, in process order, to OUT — 90° modules only, never crossing itself.", _small);
            OrderList();
            bool opt = GUILayout.Toggle(_optimize, "  Let the tool pick the order (O)", _toggle);
            if (opt != _optimize) { _optimize = opt; Recompute(); }
            var stations = _layout.Stations.Select(s => s.Id).ToList();
            if (_optimize && _route != null && _route.Success && !_route.StationOrder.SequenceEqual(stations))
                if (Button("Use this order:  " + string.Join(" → ", _route.StationOrder))) AdoptOrder();
            if (Button("Edit this route by hand")) EditAutoRoute();
        }
        else
        {
            GUILayout.BeginVertical(_card);
            GUILayout.Label("<b>Press on IN and drag</b> across the floor. Drag back to undo. Press anywhere on the belt to redraw from there; right-click cuts it off.", _body);
            GUILayout.EndVertical();
            GUILayout.BeginHorizontal();
            if (Button("Finish automatically")) FinishAutomatically();
            if (Button("Clear")) { Snapshot(); _layout.ManualPath.Clear(); Recompute(); }
            GUILayout.EndHorizontal();
            if (_route != null && _route.StationOrder.Count > 0)
                GUILayout.Label("Served so far: " + string.Join(" → ", _route.StationOrder), _small);
        }

        GUILayout.Label("Result", _h2);
        if (_route == null) { }
        else if (_route.Success)
        {
            GUILayout.Label($"<b>{_metrics.ConveyorLengthM:0.0} m</b> of conveyor, {_metrics.ConveyorModules} modules: {_metrics.Straights} straight, {_metrics.Lefts} left, {_metrics.Rights} right.", _body);
            GUILayout.Label($"IN → {string.Join(" → ", _route.StationOrder)} → OUT", _small);
            GUILayout.Label($"{_metrics.CadInstances} parts in total · floor use {_metrics.FloorUse:P0}", _small);
        }
        else if (_route.Manual)
        {
            foreach (var p in _route.Problems.Take(5))
                GUILayout.Label("• " + p, p.StartsWith("Not finished") ? _warn : _err);
        }
        else GUILayout.Label(_route.Failure + Hint(), _err);

        GUILayout.Space(10);
        if (Button("Next: see it in 3D  →")) SetStep(Step.View3D);
    }

    string Hint()
    {
        if (_route == null || _route.Success) return "";
        if (_route.Failure.StartsWith("The layout has errors")) return "\nFix the errors under Checks first.";
        return _optimize ? "\nTry moving a station, or draw part of the belt yourself." : "\nTry \"Let the tool pick the order\", or draw part of the belt yourself.";
    }

    void ViewPanel()
    {
        GUILayout.Label("3D view", _h2);
        GUILayout.Label("Right-drag to orbit · Shift+right-drag or middle-drag to pan · wheel to zoom.", _small);
        if (_route == null || !_route.Success)
            GUILayout.Label("The conveyor is not finished, so no products run. Go back to step 3 to finish it.", _warn);
        bool zones = GUILayout.Toggle(_view.ShowZones3D, "  Show work zones", _toggle);
        if (zones != _view.ShowZones3D) { _view.ShowZones3D = zones; Recompute(); }
        _view.Animate = GUILayout.Toggle(_view.Animate, "  Run the line (products and robots)", _toggle);
        if (Button("Reset camera")) { _yaw = -32; _pitch = 56; _zoom = 1.12f; _panOffset = Vector3.zero; }
        GUILayout.Space(10);
        if (Button("←  Back to the blueprint (Tab)")) SetStep(_lastBlueprintStep);
    }

    // ---- checks, status, load

    void DrawChecks(Rect r)
    {
        GUI.Box(r, GUIContent.none, _card);
        int errs = _issues.Count(i => i.Severity == Severity.Error), warns = _issues.Count - errs;
        string head = errs + warns == 0 ? "Checks — all clear" : $"Checks — {errs} error{(errs == 1 ? "" : "s")}, {warns} warning{(warns == 1 ? "" : "s")}";
        GUI.Label(new Rect(r.x + 10, r.y + 6, r.width - 20, 22), head, new GUIStyle(_h2) { margin = new RectOffset() });
        GUILayout.BeginArea(new Rect(r.x + 6, r.y + 30, r.width - 12, r.height - 34));
        _checksScroll = GUILayout.BeginScrollView(_checksScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
        if (_issues.Count == 0) GUILayout.Label("No errors, no warnings. Everything fits.", _ok);
        foreach (var i in _issues)
        {
            var style = new GUIStyle(_issueBtn) { normal = { textColor = i.Severity == Severity.Error ? FactoryView.Bad : FactoryView.Warn } };
            string text = (i.Severity == Severity.Error ? "● " : "▲ ") + i.Message + (i.Fix != null ? $"\n<color=#9FB4D0><size=12>{i.Fix}</size></color>" : "");
            style.richText = true;
            if (GUILayout.Button(text, style)) FocusIssue(i);
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    void StatusBar(float vw, float vh)
    {
        string left;
        if (_morph > 0.5f) left = "3D view · Tab returns to the blueprint";
        else if (_hover == null) left = "Hover over the floor for details";
        else
        {
            var c = _hover.Value;
            left = $"<b>{RouteChecker.Name(c)}</b>";
            if (!_layout.IsFloor(c)) left += " · not floor";
            var on = _layout.BodyAt(c);
            if (on != null) left += $" · {on.Id} {on.DisplayName}";
            foreach (var z in _layout.Components.Where(x => x.WorkCells().Contains(c)))
                left += z.Kind == ComponentKind.Robot ? $" · in {z.Id}'s work envelope" : z.Kind == ComponentKind.Drill ? $" · {z.Id}'s work cell" : $" · {z.Id}'s loading side";
            if (_route != null && _route.Cells.Contains(c)) left += " · conveyor";
        }
        string right = _route != null && _route.Success
            ? $"Conveyor {_metrics.ConveyorLengthM:0.0} m · {_metrics.ConveyorModules} modules · {_metrics.CadInstances} parts"
            : $"{_layout.Components.Count} components · {_layout.Width * _layout.CellMm / 1000f:0.#} × {_layout.Height * _layout.CellMm / 1000f:0.#} m";
        var bar = new Rect(0, vh - StatusH, vw, StatusH);
        GUI.Label(bar, left, _status);
        GUI.Label(bar, right, new GUIStyle(_status) { alignment = TextAnchor.MiddleRight, normal = { background = null, textColor = TextMuted } });
    }

    void LoadPopup(float vw, float vh)
    {
        var r = LoadRect(vw, vh);
        GUI.Box(r, GUIContent.none, _tourCard);
        GUILayout.BeginArea(new Rect(r.x + 16, r.y + 14, r.width - 32, r.height - 28));
        GUILayout.Label("Open a layout", _h2);
        _loadScroll = GUILayout.BeginScrollView(_loadScroll);
        var files = Directory.Exists(LayoutsDir) ? Directory.GetFiles(LayoutsDir, "*.json").Where(f => !f.EndsWith(".plan.json")).ToArray() : new string[0];
        foreach (var f in files)
            if (Button(Path.GetFileNameWithoutExtension(f)))
            {
                Snapshot();
                if (TryLoad(f)) { Recompute(); Say("Opened " + Path.GetFileName(f)); }
                _showLoad = false;
            }
        GUILayout.EndScrollView();
        GUILayout.BeginHorizontal();
        if (Button("New empty layout"))
        {
            Snapshot();
            _layout = new Layout(_layout.Width, _layout.Height, "new-line");
            _layoutPath = null; _selected = null; _showLoad = false;
            SetStep(Step.Floor);
            Recompute();
        }
        if (Button("Close")) _showLoad = false;
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    void SwapStations(Component a, Component b)
    {
        Snapshot();
        int ia = _layout.Components.IndexOf(a), ib = _layout.Components.IndexOf(b);
        _layout.Components[ia] = b; _layout.Components[ib] = a;
        Recompute();
    }

    void AdoptOrder()
    {
        Snapshot();
        var order = _route.StationOrder.Select(_layout.Find).ToList();
        var rest = _layout.Components.Where(c => !c.IsStation).ToList();
        _layout.Components.Clear();
        _layout.Components.AddRange(rest);
        _layout.Components.AddRange(order);
        Recompute();
    }

    /// <summary>Drawing-style labels: column letters, row numbers, ids and machine names. Blueprint only.</summary>
    void Labels(float sc)
    {
        if (_morph > 0.3f) return;
        var a = 1 - _morph / 0.3f;
        var st = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter, wordWrap = false, normal = { textColor = new Color(0.9f, 0.95f, 1f, a) } };
        float s = _layout.CellMm / 1000f;
        Vector2 Scr(Vector3 w) { var p = _cam.WorldToScreenPoint(w); return new Vector2(p.x, Screen.height - p.y) / sc; }
        for (int x = 0; x < _layout.Width; x++)
        {
            var p = Scr(new Vector3((x + 0.5f) * s, 0, -0.22f));
            GUI.Label(new Rect(p.x - 15, p.y - 9, 30, 18), BlueprintSvg.ColumnName(x), st);
        }
        for (int y = 0; y < _layout.Height; y++)
        {
            var p = Scr(new Vector3(-0.22f, 0, (y + 0.5f) * s));
            GUI.Label(new Rect(p.x - 15, p.y - 9, 30, 18), (y + 1).ToString(), st);
        }
        var idStyle = new GUIStyle(st) { fontSize = 11, alignment = TextAnchor.UpperRight };
        var nameStyle = new GUIStyle(st) { fontSize = 13, fontStyle = FontStyle.Bold, wordWrap = true };
        foreach (var c in _layout.Components)
        {
            var body = c.BodyCells().ToList();
            if (c.Kind == ComponentKind.Start || c.Kind == ComponentKind.Stop)
            {
                var p0 = Scr(_view.CellCentre(c.Origin) + new Vector3(0, 0, -s * 0.62f));
                GUI.Label(new Rect(p0.x - 20, p0.y - 8, 40, 16), c.Kind == ComponentKind.Start ? "IN" : "OUT", st);
                continue;
            }
            var p = Scr(new Vector3((body.Max(b => b.X) + 1) * s - 0.03f, 0, (body.Max(b => b.Y) + 1) * s - 0.02f));
            GUI.Label(new Rect(p.x - 60, p.y, 60, 16), c.Id, idStyle);
            if (c.Kind == ComponentKind.Machine || !string.IsNullOrEmpty(c.Label))
            {
                var lo = Scr(new Vector3(body.Min(b => b.X) * s, 0, body.Min(b => b.Y) * s));
                var hi = Scr(new Vector3((body.Max(b => b.X) + 1) * s, 0, (body.Max(b => b.Y) + 1) * s));
                var rect = Rect.MinMaxRect(Mathf.Min(lo.x, hi.x), Mathf.Min(lo.y, hi.y), Mathf.Max(lo.x, hi.x), Mathf.Max(lo.y, hi.y));
                if (c.Kind == ComponentKind.Machine) GUI.Label(rect, c.DisplayName, nameStyle);
                else GUI.Label(new Rect(rect.x - 30, rect.yMax, rect.width + 60, 16), c.Label, st);
            }
        }
    }

    // ================================================================== guided tour

    sealed class TourPage
    {
        public string Title, Body;
        public Step? Step;
        public string Highlight; // "tabs", "content", "checks", "footer", "floor", null
    }

    static readonly TourPage[] TourPages =
    {
        new TourPage { Title = "Welcome to Factory Layout", Body = "Plan a production line in four steps: draw the floor, place the equipment, lay the conveyor, and see it in 3D.\n\nEvery step is checked while you work, so mistakes show up on the drawing — not on the shop floor." },
        new TourPage { Title = "Four steps", Body = "Work through the tabs from left to right. You can go back to any step at any time; nothing is lost. The number in brackets counts problems in that step.", Highlight = "tabs" },
        new TourPage { Title = "1 · Floor", Body = "Set the hall's width and depth in metres. The grid is 0.5 m, so 3.5 × 3.5 m is 7 × 7 tiles.\n\nUse Remove floor to cut out columns, shafts or an L-shaped corner.", Step = FactoryApp.Step.Floor, Highlight = "content" },
        new TourPage { Title = "2 · Equipment", Body = "Pick a tool and click on the floor. R rotates, drag moves, right-click removes.\n\nYellow hatching is space the equipment needs: a robot arm needs the 3×3 around it, a drill works on the tile in front of it, and a machine is loaded from its yellow side. Machines can be any size — select one to set it.", Step = FactoryApp.Step.Equipment, Highlight = "content" },
        new TourPage { Title = "Process order", Body = "The list under the tools is the order the products visit the stations. Use the arrows to change it. The conveyor follows this order.", Step = FactoryApp.Step.Equipment, Highlight = "content" },
        new TourPage { Title = "3 · Conveyor", Body = "Let the tool route the belt, or draw it yourself: press on IN and drag. You can draw the tricky part yourself and let the tool finish the rest.", Step = FactoryApp.Step.Conveyor, Highlight = "content" },
        new TourPage { Title = "Checks", Body = "Problems are listed here, each with what to do about it. Click one and it lights up on the drawing.", Highlight = "checks" },
        new TourPage { Title = "4 · 3D view", Body = "Press Tab or step 4 and the drawing takes 3D form. Right-drag to orbit, the wheel zooms. Products run along the conveyor when it is finished.", Highlight = "tabs" },
        new TourPage { Title = "Save your work", Body = "Save writes the layout and a blueprint drawing (.svg) next to it. Undo and Redo go back and forth (Ctrl+Z, Ctrl+Y).\n\nA− and A+ change the text size, and ? brings this tour back.", Highlight = "footer" },
    };

    void OpenTour(int page) { _showLoad = false; TourGo(Mathf.Clamp(page, 0, TourPages.Length - 1)); }

    void TourGo(int page)
    {
        if (page < 0) return;
        if (page >= TourPages.Length) { CloseTour(); return; }
        _tour = page;
        var p = TourPages[page];
        if (p.Step.HasValue && _step != p.Step.Value) SetStep(p.Step.Value);
    }

    void CloseTour()
    {
        _tour = -1;
        if (!Capturing) PlayerPrefs.SetInt("tour.done", 1);
    }

    void Tour(float vw, float vh)
    {
        var page = TourPages[_tour];
        var panel = PanelRect(vh);
        Rect? hl = null;
        switch (page.Highlight)
        {
            case "tabs": hl = TabsRect(panel); break;
            case "content": { var t = TabsRect(panel); var c = ChecksRect(panel); hl = new Rect(panel.x + 6, t.yMax + 4, panel.width - 12, c.y - t.yMax - 8); break; }
            case "checks": hl = ChecksRect(panel); break;
            case "footer": hl = new Rect(panel.x + 6, panel.yMax - 50, panel.width - 12, 44); break;
        }

        // Dim everything except the highlighted area.
        var dim = new Color(0, 0, 0, 0.55f);
        if (hl.HasValue)
        {
            var h = hl.Value;
            Swatch(new Rect(0, 0, vw, h.y), dim);
            Swatch(new Rect(0, h.yMax, vw, vh - h.yMax), dim);
            Swatch(new Rect(0, h.y, h.x, h.height), dim);
            Swatch(new Rect(h.xMax, h.y, vw - h.xMax, h.height), dim);
            float g = 3;
            Swatch(new Rect(h.x - g, h.y - g, h.width + 2 * g, g), Accent);
            Swatch(new Rect(h.x - g, h.yMax, h.width + 2 * g, g), Accent);
            Swatch(new Rect(h.x - g, h.y, g, h.height), Accent);
            Swatch(new Rect(h.xMax, h.y, g, h.height), Accent);
        }
        else Swatch(new Rect(0, 0, vw, vh), dim);

        float w = 440;
        var bodyH = _body.CalcHeight(new GUIContent(page.Body), w - 40);
        float hgt = bodyH + 140;
        float x = hl.HasValue ? Mathf.Min(hl.Value.xMax + 28, vw - w - 20) : (vw - w) / 2;
        float y = hl.HasValue ? Mathf.Clamp(hl.Value.y, 20, vh - hgt - 20) : (vh - hgt) / 2;
        var card = new Rect(x, y, w, hgt);
        GUI.Box(card, GUIContent.none, _tourCard);
        GUILayout.BeginArea(new Rect(card.x + 20, card.y + 16, card.width - 40, card.height - 28));
        GUILayout.Label($"{_tour + 1} / {TourPages.Length}", _small);
        GUILayout.Label(page.Title, _h1);
        GUILayout.Space(6);
        GUILayout.Label(page.Body, _body);
        GUILayout.FlexibleSpace();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Skip tour", new GUIStyle(_small) { padding = new RectOffset(0, 0, 8, 8) }, GUILayout.Width(80))) CloseTour();
        GUILayout.FlexibleSpace();
        if (_tour > 0 && Button("Back", false, GUILayout.Width(80))) TourGo(_tour - 1);
        if (Button(_tour == TourPages.Length - 1 ? "Start planning" : "Next", true, GUILayout.Width(_tour == TourPages.Length - 1 ? 140 : 80))) TourGo(_tour + 1);
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }
}
