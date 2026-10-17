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

    public sealed class PlaceBuildingCommand : ICreatorCommand
    {
        public readonly string DefId;
        public readonly GridPos Origin;
        public PlaceBuildingCommand(string defId, GridPos origin) { DefId = defId; Origin = origin; }
        public CommandResult Execute(WorldState world) => world.TryPlaceBuilding(DefId, Origin);
    }

    public sealed class BulldozeCommand : ICreatorCommand
    {
        public readonly GridPos Cell;
        public BulldozeCommand(GridPos cell) { Cell = cell; }
        public CommandResult Execute(WorldState world) => world.TryBulldoze(Cell);
    }

    public sealed class BuildRoadCommand : ICreatorCommand
    {
        public readonly GridPos From;
        public readonly GridPos To;
        public BuildRoadCommand(GridPos from, GridPos to) { From = from; To = to; }
        public CommandResult Execute(WorldState world) => world.TryBuildRoad(From, To);
    }

    public sealed class RaiseTerrainCommand : ICreatorCommand
    {
        public readonly int VertexX, VertexZ;
        public readonly float Radius, Delta;

        public RaiseTerrainCommand(int vertexX, int vertexZ, float radius, float delta)
        {
            VertexX = vertexX; VertexZ = vertexZ; Radius = radius; Delta = delta;
        }

        public CommandResult Execute(WorldState world) => world.TryRaiseTerrain(VertexX, VertexZ, Radius, Delta);
    }

    public sealed class FlattenTerrainCommand : ICreatorCommand
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

namespace WorldSim.Core.Commands
{
    using WorldSim.Core.Systems;

    /// <summary>Changes one city policy (tax rates, subsidies, budgets). See Policy.Keys.</summary>
    public sealed class SetPolicyCommand : ICreatorCommand
    {
        public readonly string Key;
        public readonly double Value;
        public SetPolicyCommand(string key, double value) { Key = key; Value = value; }

        public CommandResult Execute(WorldState world)
        {
            string error = world.Economy.City.Policy.Set(Key, Value);
            if (error != null) return CommandResult.Fail(error);
            world.Log("政策", $"政策を変更しました：{Key} = {Value}");
            return CommandResult.Ok("政策を変更しました");
        }
    }

    /// <summary>Chooses the technology the city's labs work on next.</summary>
    public sealed class SetResearchCommand : ICreatorCommand
    {
        public readonly string TechId;
        public SetResearchCommand(string techId) { TechId = techId; }

        public CommandResult Execute(WorldState world)
        {
            if (!world.Content.TryGetTech(TechId, out var tech)) return CommandResult.Fail($"未定義の技術 '{TechId}'");
            if (!ResearchSystem.Available(world, TechId)) return CommandResult.Fail($"「{tech.Name}」は研究済みか前提技術が未完了です");
            var rs = world.Economy.City.Research;
            if (rs.Current != TechId) rs.Progress = 0;
            rs.Current = TechId;
            return CommandResult.Ok($"研究目標を「{tech.Name}」にしました");
        }
    }

    /// <summary>Starts a disaster now (scenario play and testing).</summary>
    public sealed class TriggerDisasterCommand : ICreatorCommand
    {
        public readonly DisasterKind Kind;
        public readonly GridPos Epicenter;
        public readonly double Magnitude;

        public TriggerDisasterCommand(DisasterKind kind, GridPos epicenter, double magnitude)
        {
            Kind = kind; Epicenter = epicenter; Magnitude = magnitude;
        }

        public CommandResult Execute(WorldState world)
        {
            if (Kind == DisasterKind.Flood)
            {
                if (Magnitude <= 0 || Magnitude > 10) return CommandResult.Fail("洪水の水位上昇は0〜10m");
                DisasterSystem.Flood(world, world.Terrain.SeaLevel + (float)Magnitude);
            }
            else
            {
                if (Magnitude < 4 || Magnitude > 9) return CommandResult.Fail("マグニチュードは4〜9");
                if (!world.Terrain.InBounds(Epicenter)) return CommandResult.Fail("震源が範囲外です");
                DisasterSystem.Earthquake(world, Epicenter, Magnitude);
            }
            return CommandResult.Ok("災害が発生しました");
        }
    }
}
