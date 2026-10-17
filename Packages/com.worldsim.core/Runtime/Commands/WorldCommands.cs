using WorldSim.Core.World;

namespace WorldSim.Core.Commands
{
    /// <summary>
    /// A player intent expressed as plain data. Input code builds commands instead of mutating the
    /// world directly, which keeps a single path for validation and later allows recording,
    /// replay and server-side authority (spec 5.5) without changing the UI.
    /// </summary>
    public interface IWorldCommand
    {
        CommandResult Execute(WorldState world);
    }

    public sealed class PlaceBuildingCommand : IWorldCommand
    {
        public readonly string DefId;
        public readonly GridPos Origin;
        public PlaceBuildingCommand(string defId, GridPos origin) { DefId = defId; Origin = origin; }
        public CommandResult Execute(WorldState world) => world.TryPlaceBuilding(DefId, Origin);
    }

    public sealed class BulldozeCommand : IWorldCommand
    {
        public readonly GridPos Cell;
        public BulldozeCommand(GridPos cell) { Cell = cell; }
        public CommandResult Execute(WorldState world) => world.TryBulldoze(Cell);
    }

    public sealed class BuildRoadCommand : IWorldCommand
    {
        public readonly GridPos From;
        public readonly GridPos To;
        public BuildRoadCommand(GridPos from, GridPos to) { From = from; To = to; }
        public CommandResult Execute(WorldState world) => world.TryBuildRoad(From, To);
    }

    public sealed class RaiseTerrainCommand : IWorldCommand
    {
        public readonly int VertexX, VertexZ;
        public readonly float Radius, Delta;

        public RaiseTerrainCommand(int vertexX, int vertexZ, float radius, float delta)
        {
            VertexX = vertexX; VertexZ = vertexZ; Radius = radius; Delta = delta;
        }

        public CommandResult Execute(WorldState world) => world.TryRaiseTerrain(VertexX, VertexZ, Radius, Delta);
    }

    public sealed class FlattenTerrainCommand : IWorldCommand
    {
        public readonly int VertexX, VertexZ;
        public readonly float Radius, TargetHeight;

        public FlattenTerrainCommand(int vertexX, int vertexZ, float radius, float targetHeight)
        {
            VertexX = vertexX; VertexZ = vertexZ; Radius = radius; TargetHeight = targetHeight;
        }

        public CommandResult Execute(WorldState world) => world.TryFlattenTerrain(VertexX, VertexZ, Radius, TargetHeight);
    }
}
