using WorldSim.Core.Content;

namespace WorldSim.Core.World
{
    /// <summary>Play-state instance of a building. Identified by a stable Id that survives save/load.</summary>
    public sealed class Building
    {
        public int Id { get; }
        public BuildingDef Def { get; }
        public GridPos Origin { get; }
        /// <summary>Ground floor elevation: the highest terrain vertex under the footprint.</summary>
        public float BaseHeight { get; internal set; }
        /// <summary>0..1 structural condition. Always 1 until the disaster system exists (phase 4).</summary>
        public float Condition { get; internal set; } = 1f;

        public Building(int id, BuildingDef def, GridPos origin, float baseHeight)
        {
            Id = id;
            Def = def;
            Origin = origin;
            BaseHeight = baseHeight;
        }

        public GridRect Footprint => GridRect.FromSize(Origin, Def.Width, Def.Depth);
    }
}
