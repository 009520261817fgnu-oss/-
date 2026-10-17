using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using WorldSim.Core;
using WorldSim.Core.Commands;
using WorldSim.Core.Content;
using WorldSim.Core.World;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace WorldSim.Presentation
{
    public enum ToolKind
    {
        Inspect,
        Build,
        Road,
        Bulldoze,
        Raise,
        Lower,
        Flatten,
    }

    /// <summary>
    /// Turns pointer input (mouse or single touch) into world commands. Shows a ghost preview that is
    /// green when the core says the action is valid and red otherwise.
    /// </summary>
    public sealed class ToolController : MonoBehaviour
    {
        private const float TerrainBrushRadius = 3f;
        private const float TerrainBrushRate = 12f; // metres per second at the brush centre

        public ToolKind Tool { get; private set; } = ToolKind.Inspect;
        public BuildingDef SelectedBuilding { get; private set; }
        public Building InspectedBuilding { get; private set; }
        public GridPos? HoverCell { get; private set; }
        public GridPos? RoadStart { get; private set; }

        private WorldSimGame _game;
        private Camera _camera;
        private readonly List<GameObject> _ghosts = new List<GameObject>();
        private Material _ghostOk, _ghostBad;
        private float _flattenTarget;
        private bool _pointerWasDown;

        public void Init(WorldSimGame game, Camera cam)
        {
            _game = game;
            _camera = cam;
            _ghostOk = Materials.Create(new Color(0.3f, 0.9f, 0.3f));
            _ghostBad = Materials.Create(new Color(0.95f, 0.25f, 0.2f));
            foreach (BuildingDef d in game.World.Content.Buildings) { SelectedBuilding = d; break; }
        }

        public void SetTool(ToolKind tool, BuildingDef building = null)
        {
            Tool = tool;
            if (building != null) SelectedBuilding = building;
            RoadStart = null;
            if (tool != ToolKind.Inspect) InspectedBuilding = null;
        }

        private void Update()
        {
            _game.CameraRig.AllowSingleTouchPan = Tool == ToolKind.Inspect;
            HideGhosts();
            Keyboard kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) SetTool(ToolKind.Inspect);

            if (!ReadPointer(out Vector2 screen, out bool down, out bool pressed) || _game.Hud.IsOverUi(screen))
            {
                HoverCell = null;
                _pointerWasDown = down;
                return;
            }
            _pointerWasDown = down;

            if (!Physics.Raycast(_camera.ScreenPointToRay(screen), out RaycastHit hit, 10000f))
            {
                HoverCell = null;
                return;
            }
            Terrain t = _game.World.Terrain;
            GridPos cell = t.WorldToCell(hit.point.x, hit.point.z);
            if (!t.InBounds(cell)) { HoverCell = null; return; }
            HoverCell = cell;
            int vx = Mathf.RoundToInt(hit.point.x / t.CellSize), vz = Mathf.RoundToInt(hit.point.z / t.CellSize);

            switch (Tool)
            {
                case ToolKind.Inspect:
                    if (pressed) InspectedBuilding = _game.World.GetBuildingAt(cell);
                    break;

                case ToolKind.Build:
                    if (SelectedBuilding == null) break;
                    var origin = new GridPos(cell.X - (SelectedBuilding.Width - 1) / 2, cell.Z - (SelectedBuilding.Depth - 1) / 2);
                    bool ok = _game.World.CanPlaceBuilding(SelectedBuilding, origin).Success;
                    ShowGhost(GridRect.FromSize(origin, SelectedBuilding.Width, SelectedBuilding.Depth), SelectedBuilding.Height, ok);
                    if (pressed) _game.Execute(new PlaceBuildingCommand(SelectedBuilding.Id, origin));
                    break;

                case ToolKind.Road:
                    if (RoadStart.HasValue)
                    {
                        foreach (GridPos c in WorldState.RoadPath(RoadStart.Value, cell))
                            ShowGhost(GridRect.FromSize(c, 1, 1), 0.5f, CanRoad(c));
                        if (pressed)
                        {
                            if (_game.Execute(new BuildRoadCommand(RoadStart.Value, cell)).Success) RoadStart = null;
                        }
                    }
                    else
                    {
                        ShowGhost(GridRect.FromSize(cell, 1, 1), 0.5f, CanRoad(cell));
                        if (pressed) RoadStart = cell;
                    }
                    break;

                case ToolKind.Bulldoze:
                    Building b = _game.World.GetBuildingAt(cell);
                    ShowGhost(b != null ? b.Footprint : GridRect.FromSize(cell, 1, 1), b != null ? b.Def.Height + 1 : 0.5f,
                        b != null || _game.World.HasRoad(cell));
                    if (pressed) _game.Execute(new BulldozeCommand(cell));
                    break;

                case ToolKind.Raise:
                case ToolKind.Lower:
                case ToolKind.Flatten:
                    ShowGhost(new GridRect(vx - 3, vz - 3, vx + 3, vz + 3).Clamp(t.Width, t.Depth), 0.3f, true);
                    if (pressed && t.VertexInBounds(vx, vz)) _flattenTarget = t.GetVertexHeight(vx, vz);
                    if (down)
                    {
                        float amount = TerrainBrushRate * Time.unscaledDeltaTime;
                        IWorldCommand cmd = Tool == ToolKind.Raise ? new RaiseTerrainCommand(vx, vz, TerrainBrushRadius, amount)
                            : Tool == ToolKind.Lower ? new RaiseTerrainCommand(vx, vz, TerrainBrushRadius, -amount)
                            : (IWorldCommand)new FlattenTerrainCommand(vx, vz, TerrainBrushRadius + 1, _flattenTarget);
                        _game.Execute(cmd);
                    }
                    break;
            }
        }

        private bool CanRoad(GridPos c)
        {
            WorldState w = _game.World;
            if (!w.Terrain.InBounds(c)) return false;
            if (w.HasRoad(c)) return true;
            return !w.IsOccupied(c) && !w.Terrain.IsCellUnderwater(c) &&
                   w.Terrain.CellMaxHeight(c) - w.Terrain.CellMinHeight(c) <= WorldState.RoadMaxSlope;
        }

        /// <summary>Mouse, or the first touch when exactly one finger is down (two fingers = camera).</summary>
        private bool ReadPointer(out Vector2 screen, out bool down, out bool pressed)
        {
            var touches = ETouch.activeTouches;
            if (touches.Count == 1)
            {
                screen = touches[0].screenPosition;
                down = true;
                pressed = touches[0].phase == UnityEngine.InputSystem.TouchPhase.Began;
                return true;
            }
            if (touches.Count > 1)
            {
                screen = default;
                down = pressed = false;
                return false;
            }
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                screen = default;
                down = pressed = false;
                return false;
            }
            screen = mouse.position.ReadValue();
            down = mouse.leftButton.isPressed;
            pressed = mouse.leftButton.wasPressedThisFrame && !_pointerWasDown;
            return true;
        }

        private void ShowGhost(GridRect rect, float height, bool ok)
        {
            Terrain t = _game.World.Terrain;
            GridRect r = rect.Clamp(t.Width, t.Depth);
            if (r.IsEmpty) return;
            t.HeightRange(r, out float min, out float max);
            GameObject g = GetGhost();
            g.GetComponent<Renderer>().sharedMaterial = ok ? _ghostOk : _ghostBad;
            g.transform.position = new Vector3((r.MinX + r.Width * 0.5f) * t.CellSize, (min + max + height) * 0.5f,
                (r.MinZ + r.Depth * 0.5f) * t.CellSize);
            g.transform.localScale = new Vector3(r.Width * t.CellSize, max - min + height, r.Depth * t.CellSize);
            g.SetActive(true);
        }

        private GameObject GetGhost()
        {
            foreach (GameObject g in _ghosts)
                if (!g.activeSelf) return g;
            GameObject ghost = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ghost.name = "Ghost";
            Destroy(ghost.GetComponent<Collider>());
            ghost.transform.SetParent(transform, false);
            _ghosts.Add(ghost);
            return ghost;
        }

        private void HideGhosts()
        {
            foreach (GameObject g in _ghosts) g.SetActive(false);
        }
    }
}
