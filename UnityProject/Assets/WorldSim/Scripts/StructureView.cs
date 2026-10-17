using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using WorldSim.Core;
using WorldSim.Core.World;

namespace WorldSim.Presentation
{
    /// <summary>
    /// Renders buildings (one box per building), roads (one combined mesh) and trees (one combined
    /// mesh). Box buildings are placeholders until the modular building system (spec 2.2).
    /// </summary>
    public sealed class StructureView : MonoBehaviour
    {
        private const float RoadLift = 0.15f;

        private WorldState _world;
        private readonly Dictionary<int, GameObject> _buildings = new Dictionary<int, GameObject>();
        private Material _buildingMaterial;
        private MaterialPropertyBlock _block;
        private Mesh _roadMesh;
        private Mesh _treeMesh;
        private bool _buildingsDirty, _roadsDirty, _treesDirty, _rebaseBuildings;

        public void Init(WorldState world)
        {
            _world = world;
            _buildingMaterial = Materials.Create(Color.white);
            _block = new MaterialPropertyBlock();
            _roadMesh = CreateMeshObject("Roads", new Color(0.30f, 0.30f, 0.32f));
            _treeMesh = CreateMeshObject("Trees", new Color(0.18f, 0.36f, 0.16f));
            _buildingsDirty = _roadsDirty = _treesDirty = true;
        }

        public void MarkDirty(WorldChangeKind kind)
        {
            if (kind == WorldChangeKind.Buildings) _buildingsDirty = true;
            else if (kind == WorldChangeKind.Roads) _roadsDirty = true;
            else if (kind == WorldChangeKind.Vegetation) _treesDirty = true;
        }

        public void MarkAllDirty()
        {
            _buildingsDirty = _roadsDirty = _treesDirty = _rebaseBuildings = true;
        }

        private void LateUpdate()
        {
            if (_buildingsDirty) SyncBuildings();
            if (_roadsDirty) RebuildRoads();
            if (_treesDirty) RebuildTrees();
            _buildingsDirty = _roadsDirty = _treesDirty = _rebaseBuildings = false;
        }

        private Mesh CreateMeshObject(string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = Materials.Create(color);
            return mesh;
        }

        private void SyncBuildings()
        {
            var alive = new HashSet<int>();
            foreach (Building b in _world.Buildings)
            {
                alive.Add(b.Id);
                if (_buildings.TryGetValue(b.Id, out GameObject existing))
                {
                    if (_rebaseBuildings) Place(existing, b);
                    continue;
                }
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(go.GetComponent<Collider>()); // picking is done on the terrain grid
                go.name = $"{b.Def.Id}#{b.Id}";
                go.transform.SetParent(transform, false);
                Renderer r = go.GetComponent<Renderer>();
                r.sharedMaterial = _buildingMaterial;
                _block.Clear();
                Materials.SetColor(_block, Materials.FromRgb(b.Def.Color));
                r.SetPropertyBlock(_block);
                Place(go, b);
                _buildings.Add(b.Id, go);
            }

            var removed = new List<int>();
            foreach (int id in _buildings.Keys)
                if (!alive.Contains(id)) removed.Add(id);
            foreach (int id in removed)
            {
                Destroy(_buildings[id]);
                _buildings.Remove(id);
            }
        }

        private void Place(GameObject go, Building b)
        {
            Terrain t = _world.Terrain;
            t.HeightRange(b.Footprint, out float min, out _);
            float bottom = min - 0.5f; // foundation reaches below the lowest corner
            float top = b.BaseHeight + b.Def.Height;
            float inset = 0.6f; // gap between neighbouring buildings
            go.transform.localPosition = new Vector3(
                (b.Origin.X + b.Def.Width * 0.5f) * t.CellSize,
                (bottom + top) * 0.5f,
                (b.Origin.Z + b.Def.Depth * 0.5f) * t.CellSize);
            go.transform.localScale = new Vector3(b.Def.Width * t.CellSize - inset, top - bottom, b.Def.Depth * t.CellSize - inset);
        }

        private void RebuildRoads()
        {
            Terrain t = _world.Terrain;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            float s = t.CellSize;
            for (int z = 0; z < t.Depth; z++)
            for (int x = 0; x < t.Width; x++)
            {
                if (!_world.HasRoad(new GridPos(x, z))) continue;
                int i = verts.Count;
                verts.Add(new Vector3(x * s, t.GetVertexHeight(x, z) + RoadLift, z * s));
                verts.Add(new Vector3((x + 1) * s, t.GetVertexHeight(x + 1, z) + RoadLift, z * s));
                verts.Add(new Vector3(x * s, t.GetVertexHeight(x, z + 1) + RoadLift, (z + 1) * s));
                verts.Add(new Vector3((x + 1) * s, t.GetVertexHeight(x + 1, z + 1) + RoadLift, (z + 1) * s));
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 1);
                tris.Add(i + 1); tris.Add(i + 2); tris.Add(i + 3);
            }
            Apply(_roadMesh, verts, tris);
        }

        private void RebuildTrees()
        {
            Terrain t = _world.Terrain;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int z = 0; z < t.Depth; z++)
            for (int x = 0; x < t.Width; x++)
            {
                byte density = _world.GetVegetation(new GridPos(x, z));
                if (density == 0) continue;
                // Deterministic jitter so the forest does not look like a grid.
                int hash = (x * 73856093) ^ (z * 19349663);
                float jx = ((hash & 255) / 255f - 0.5f) * 0.5f;
                float jz = (((hash >> 8) & 255) / 255f - 0.5f) * 0.5f;
                float wx = (x + 0.5f + jx) * t.CellSize, wz = (z + 0.5f + jz) * t.CellSize;
                float height = 5f + density / 255f * 7f;
                AddPyramid(verts, tris, new Vector3(wx, t.SampleHeight(wx, wz), wz), height * 0.35f, height);
            }
            Apply(_treeMesh, verts, tris);
        }

        private static void AddPyramid(List<Vector3> verts, List<int> tris, Vector3 basePos, float radius, float height)
        {
            Vector3 top = basePos + Vector3.up * height;
            Vector3[] corners =
            {
                basePos + new Vector3(-radius, 0, -radius), basePos + new Vector3(radius, 0, -radius),
                basePos + new Vector3(radius, 0, radius), basePos + new Vector3(-radius, 0, radius),
            };
            for (int k = 0; k < 4; k++)
            {
                // Separate vertices per face for flat shading.
                int i = verts.Count;
                verts.Add(corners[k]);
                verts.Add(top);
                verts.Add(corners[(k + 1) % 4]);
                tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
            }
        }

        private static void Apply(Mesh mesh, List<Vector3> verts, List<int> tris)
        {
            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }
    }
}
