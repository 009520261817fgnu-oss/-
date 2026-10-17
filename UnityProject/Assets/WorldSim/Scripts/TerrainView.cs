using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using WorldSim.Core;
using WorldSim.Core.World;

namespace WorldSim.Presentation
{
    /// <summary>
    /// Renders the heightfield as one mesh with a per-cell colour texture, plus an opaque sea plane.
    /// Rebuilt lazily (at most once per frame) after terrain edits. Chunking/LOD comes with larger worlds.
    /// </summary>
    public sealed class TerrainView : MonoBehaviour
    {
        private WorldState _world;
        private Mesh _mesh;
        private Texture2D _texture;
        private MeshCollider _collider;
        private bool _dirty;

        public void Init(WorldState world)
        {
            _world = world;
            Terrain t = world.Terrain;
            _mesh = new Mesh { name = "Terrain", indexFormat = IndexFormat.UInt32 };
            _texture = new Texture2D(t.Width, t.Depth, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            gameObject.AddComponent<MeshRenderer>().sharedMaterial = Materials.Create(_texture);
            _collider = gameObject.AddComponent<MeshCollider>();

            BuildTopology();
            Rebuild();
            CreateSea();
        }

        public void MarkDirty() => _dirty = true;

        private void LateUpdate()
        {
            if (!_dirty) return;
            _dirty = false;
            Rebuild();
        }

        private void BuildTopology()
        {
            Terrain t = _world.Terrain;
            var uvs = new List<Vector2>(t.VertexCountX * t.VertexCountZ);
            for (int vz = 0; vz <= t.Depth; vz++)
            for (int vx = 0; vx <= t.Width; vx++)
                uvs.Add(new Vector2((float)vx / t.Width, (float)vz / t.Depth));

            var tris = new List<int>(t.Width * t.Depth * 6);
            int stride = t.VertexCountX;
            for (int z = 0; z < t.Depth; z++)
            for (int x = 0; x < t.Width; x++)
            {
                int i = z * stride + x;
                tris.Add(i); tris.Add(i + stride); tris.Add(i + 1);
                tris.Add(i + 1); tris.Add(i + stride); tris.Add(i + stride + 1);
            }
            _mesh.SetVertices(Positions());
            _mesh.SetUVs(0, uvs);
            _mesh.SetTriangles(tris, 0);
        }

        private List<Vector3> Positions()
        {
            Terrain t = _world.Terrain;
            var verts = new List<Vector3>(t.VertexCountX * t.VertexCountZ);
            for (int vz = 0; vz <= t.Depth; vz++)
            for (int vx = 0; vx <= t.Width; vx++)
                verts.Add(new Vector3(vx * t.CellSize, t.GetVertexHeight(vx, vz), vz * t.CellSize));
            return verts;
        }

        private void Rebuild()
        {
            Terrain t = _world.Terrain;
            _mesh.SetVertices(Positions());
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            _collider.sharedMesh = null;
            _collider.sharedMesh = _mesh;

            var pixels = new Color32[t.Width * t.Depth];
            for (int z = 0; z < t.Depth; z++)
            for (int x = 0; x < t.Width; x++)
                pixels[z * t.Width + x] = GroundColor(t, new GridPos(x, z));
            _texture.SetPixels32(pixels);
            _texture.Apply(false);
        }

        private static Color32 GroundColor(Terrain t, GridPos c)
        {
            float min = t.CellMinHeight(c), max = t.CellMaxHeight(c);
            float a = (min + max) * 0.5f - t.SeaLevel;
            Color col;
            if (a <= 0) col = new Color(0.76f, 0.70f, 0.52f);
            else if (a < 1.5f) col = new Color(0.85f, 0.78f, 0.56f);
            else if (max - min > 8f) col = new Color(0.52f, 0.50f, 0.46f); // cliffs
            else if (a < 25f) col = Color.Lerp(new Color(0.50f, 0.69f, 0.38f), new Color(0.37f, 0.55f, 0.28f), a / 25f);
            else if (a < 55f) col = Color.Lerp(new Color(0.54f, 0.54f, 0.42f), new Color(0.55f, 0.50f, 0.44f), (a - 25f) / 30f);
            else if (a < 80f) col = new Color(0.60f, 0.58f, 0.56f);
            else col = new Color(0.95f, 0.95f, 0.96f);
            return col;
        }

        private void CreateSea()
        {
            Terrain t = _world.Terrain;
            GameObject sea = GameObject.CreatePrimitive(PrimitiveType.Plane); // 10x10 units
            sea.name = "Sea";
            Destroy(sea.GetComponent<Collider>());
            sea.transform.SetParent(transform, false);
            float w = t.Width * t.CellSize, d = t.Depth * t.CellSize;
            sea.transform.localPosition = new Vector3(w * 0.5f, t.SeaLevel, d * 0.5f);
            sea.transform.localScale = new Vector3(w / 10f * 1.5f, 1f, d / 10f * 1.5f);
            Material m = Materials.Create(new Color(0.16f, 0.38f, 0.62f));
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.8f);
            sea.GetComponent<MeshRenderer>().sharedMaterial = m;
        }
    }
}
