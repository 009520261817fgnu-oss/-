using System;
using UnityEngine;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Commands;
using WorldSim.Core.Content;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Presentation
{
    /// <summary>
    /// Composition root: loads content, creates the world and runner, wires up views, camera,
    /// tools and HUD. Created automatically when a scene has none, so any empty scene works.
    /// </summary>
    public sealed class WorldSimGame : MonoBehaviour
    {
        [SerializeField] private int seed = 1;

        public WorldState World { get; private set; }
        public SimulationRunner Runner { get; private set; }
        public string LastMessage { get; private set; } = "";
        public bool LastMessageFailed { get; private set; }

        public TerrainView TerrainView { get; private set; }
        public StructureView StructureView { get; private set; }
        public CameraRig CameraRig { get; private set; }
        public ToolController Tools { get; private set; }
        public WorldSimHud Hud { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (FindAnyObjectByType<WorldSimGame>() == null) new GameObject("WorldSim").AddComponent<WorldSimGame>();
        }

        private void Awake()
        {
            ContentDatabase content = LoadContent();
            World = WorldFactory.Create(content, new WorldGenOptions { Seed = (ulong)seed });
            Runner = WorldFactory.CreateRunner(World);
            Runner.Speed = SimSpeed.Normal;

            EnsureLight();
            TerrainView = new GameObject("Terrain").AddComponent<TerrainView>();
            TerrainView.transform.SetParent(transform, false);
            TerrainView.Init(World);
            StructureView = new GameObject("Structures").AddComponent<StructureView>();
            StructureView.transform.SetParent(transform, false);
            StructureView.Init(World);
            World.Changed += OnWorldChanged;

            Camera cam = Camera.main != null ? Camera.main : new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.farClipPlane = 5000f;
            CameraRig = cam.gameObject.AddComponent<CameraRig>();
            CameraRig.Init(World);

            Tools = gameObject.AddComponent<ToolController>();
            Hud = gameObject.AddComponent<WorldSimHud>();
            Tools.Init(this, cam);
            Hud.Init(this);
        }

        private void OnDestroy()
        {
            if (World != null) World.Changed -= OnWorldChanged;
        }

        private void Update()
        {
            Runner.Update(Time.unscaledDeltaTime);
        }

        public CommandResult Execute(IWorldCommand command)
        {
            CommandResult r = command.Execute(World);
            LastMessage = r.Message;
            LastMessageFailed = !r.Success;
            return r;
        }

        private void OnWorldChanged(WorldChange change)
        {
            switch (change.Kind)
            {
                case WorldChangeKind.Terrain:
                    TerrainView.MarkDirty();
                    StructureView.MarkAllDirty(); // structures and trees sit on the terrain
                    break;
                default:
                    StructureView.MarkDirty(change.Kind);
                    break;
            }
        }

        private static ContentDatabase LoadContent()
        {
            TextAsset[] assets = Resources.LoadAll<TextAsset>("WorldSimContent");
            if (assets.Length == 0) throw new InvalidOperationException("No content found under Resources/WorldSimContent");
            Array.Sort(assets, (a, b) => string.CompareOrdinal(a.name, b.name));
            var docs = new string[assets.Length];
            for (int i = 0; i < assets.Length; i++) docs[i] = assets[i].text;
            return ContentDatabase.FromJson(docs);
        }

        private static void EnsureLight()
        {
            foreach (Light l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional) return;
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }
    }
}
