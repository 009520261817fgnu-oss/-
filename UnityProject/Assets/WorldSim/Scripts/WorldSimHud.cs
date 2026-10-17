using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using WorldSim.Core.Content;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Presentation
{
    /// <summary>
    /// Prototype HUD drawn with IMGUI so it needs no prefabs or scene setup. Scales with screen
    /// height for tablets/phones. Will be replaced by UI Toolkit when the UI is designed properly.
    /// </summary>
    public sealed class WorldSimHud : MonoBehaviour
    {
        private const float BaseHeight = 900f;

        // Shown so the prototype never implies these exist yet (spec 1.2 principle 8).
        private static readonly string[] NotImplemented =
        {
            "住民・企業・生産・雇用（フェーズ2）",
            "交通量・物流・経済・税収（フェーズ3）",
            "技術・政策・環境・災害（フェーズ4）",
            "セーブ／ロード・歴史分岐・グラフ（フェーズ5）",
        };

        private WorldSimGame _game;
        private readonly List<Rect> _uiRects = new List<Rect>();
        private float _scale = 1f;
        private float _fps;
        private bool _showPlanned;

        public void Init(WorldSimGame game) => _game = game;

        /// <summary>Screen position (Input System coordinates, origin bottom-left) is over a HUD panel.</summary>
        public bool IsOverUi(Vector2 screen)
        {
            var guiPoint = new Vector2(screen.x / _scale, (Screen.height - screen.y) / _scale);
            foreach (Rect r in _uiRects)
                if (r.Contains(guiPoint)) return true;
            return false;
        }

        private void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime), 0.05f);
            Keyboard kb = Keyboard.current;
            if (kb == null) return;
            SimulationRunner runner = _game.Runner;
            if (kb.pKey.wasPressedThisFrame) runner.Speed = runner.Speed == SimSpeed.Paused ? SimSpeed.Normal : SimSpeed.Paused;
            if (kb.digit1Key.wasPressedThisFrame) runner.Speed = SimSpeed.Normal;
            if (kb.digit2Key.wasPressedThisFrame) runner.Speed = SimSpeed.Fast;
            if (kb.digit3Key.wasPressedThisFrame) runner.Speed = SimSpeed.VeryFast;
        }

        private void OnGUI()
        {
            if (_game == null || _game.World == null) return;
            _scale = Mathf.Max(1f, Screen.height / BaseHeight);
            GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1f));
            float w = Screen.width / _scale, h = Screen.height / _scale;
            _uiRects.Clear();

            DrawTopBar(w);
            DrawToolPanel(h);
            DrawInfoPanel(w, h);
        }

        private Rect Panel(Rect r)
        {
            _uiRects.Add(r);
            GUI.Box(r, GUIContent.none);
            return r;
        }

        private void DrawTopBar(float w)
        {
            Rect r = Panel(new Rect(0, 0, w, 40));
            GUILayout.BeginArea(new Rect(r.x + 8, r.y + 6, r.width - 16, 30));
            GUILayout.BeginHorizontal();
            SimulationRunner runner = _game.Runner;
            GUILayout.Label(runner.World.Clock.ToString(), GUILayout.Width(150));
            SpeedButton(runner, SimSpeed.Paused, "停止 (P)");
            SpeedButton(runner, SimSpeed.Normal, "通常 (1)");
            SpeedButton(runner, SimSpeed.Fast, "高速 (2)");
            SpeedButton(runner, SimSpeed.VeryFast, "超高速 (3)");
            GUILayout.Space(20);
            GUILayout.Label("カメラ (C):", GUILayout.Width(80));
            CameraButton(CameraMode.Overhead, "俯瞰");
            CameraButton(CameraMode.Free, "自由");
            CameraButton(CameraMode.FirstPerson, "一人称");
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{_fps:0} FPS");
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void SpeedButton(SimulationRunner runner, SimSpeed speed, string label)
        {
            if (GUILayout.Toggle(runner.Speed == speed, label, GUI.skin.button, GUILayout.Width(90))) runner.Speed = speed;
        }

        private void CameraButton(CameraMode mode, string label)
        {
            if (GUILayout.Toggle(_game.CameraRig.Mode == mode, label, GUI.skin.button, GUILayout.Width(64)))
                _game.CameraRig.SetMode(mode);
        }

        private void DrawToolPanel(float h)
        {
            ToolController tools = _game.Tools;
            Rect r = Panel(new Rect(0, 44, 180, h - 44 - 120));
            GUILayout.BeginArea(new Rect(r.x + 6, r.y + 6, r.width - 12, r.height - 12));
            GUILayout.Label("ツール (Esc: 調査)");
            ToolButton(ToolKind.Inspect, "調査");
            ToolButton(ToolKind.Road, "道路");
            ToolButton(ToolKind.Bulldoze, "撤去");
            ToolButton(ToolKind.Raise, "地形 隆起");
            ToolButton(ToolKind.Lower, "地形 掘削");
            ToolButton(ToolKind.Flatten, "地形 平坦化");
            GUILayout.Space(8);
            GUILayout.Label("建物");
            foreach (BuildingDef def in _game.World.Content.Buildings)
            {
                bool active = tools.Tool == ToolKind.Build && tools.SelectedBuilding == def;
                if (GUILayout.Toggle(active, $"{def.Name} {def.Width}x{def.Depth}", GUI.skin.button) && !active)
                    tools.SetTool(ToolKind.Build, def);
            }
            GUILayout.EndArea();
        }

        private void ToolButton(ToolKind tool, string label)
        {
            if (GUILayout.Toggle(_game.Tools.Tool == tool, label, GUI.skin.button) && _game.Tools.Tool != tool)
                _game.Tools.SetTool(tool);
        }

        private void DrawInfoPanel(float w, float h)
        {
            WorldState world = _game.World;
            ToolController tools = _game.Tools;
            Rect r = Panel(new Rect(0, h - 116, w, 116));
            GUILayout.BeginArea(new Rect(r.x + 8, r.y + 6, r.width - 16, r.height - 12));
            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(GUILayout.Width(360));
            string hover = tools.HoverCell.HasValue
                ? $"マス {tools.HoverCell.Value}  標高 {world.Terrain.CellMinHeight(tools.HoverCell.Value):0.0}m"
                : "マス -";
            GUILayout.Label(hover);
            if (tools.Tool == ToolKind.Road)
                GUILayout.Label(tools.RoadStart.HasValue ? $"道路: 始点 {tools.RoadStart.Value} → 終点をクリック" : "道路: 始点をクリック");
            Building b = tools.InspectedBuilding;
            if (b != null && world.TryGetBuilding(b.Id, out _))
                GUILayout.Label($"#{b.Id} {b.Def.Name}（{b.Def.Category}） 位置 {b.Origin}  道路接続: {(world.HasRoadAccess(b.Footprint) ? "あり" : "なし")}");
            Color prev = GUI.color;
            if (_game.LastMessageFailed) GUI.color = new Color(1f, 0.6f, 0.5f);
            GUILayout.Label(_game.LastMessage);
            GUI.color = prev;
            GUILayout.EndVertical();

            GUILayout.BeginVertical(GUILayout.Width(260));
            StatsSnapshot s = StatsHistorySystem.Capture(world);
            GUILayout.Label($"建物 {s.Buildings}  道路 {s.RoadCells} マス");
            foreach (var kv in s.ByCategory) GUILayout.Label($"  {kv.Key}: {kv.Value}");
            GUILayout.EndVertical();

            GUILayout.BeginVertical();
            _showPlanned = GUILayout.Toggle(_showPlanned, "未実装の機能を表示");
            if (_showPlanned)
                foreach (string line in NotImplemented) GUILayout.Label("・未実装: " + line);
            if (_game.Runner.DroppedTicks > 0) GUILayout.Label($"警告: 処理落ちで {_game.Runner.DroppedTicks} tick をスキップ");
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
