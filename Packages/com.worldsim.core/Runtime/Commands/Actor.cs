using WorldSim.Core.Economy;
using WorldSim.Core.World;

namespace WorldSim.Core.Commands
{
    public enum ActorRole
    {
        /// <summary>World creator / manager: edits terrain, buildings, policy, ...</summary>
        Creator,
        /// <summary>A resident living their own life (life mode): only personal actions.</summary>
        Resident,
    }

    /// <summary>Who issues a command. Permissions are checked in the core, not only hidden in the UI.</summary>
    public readonly struct Actor
    {
        public readonly ActorRole Role;
        public readonly int CitizenId;

        private Actor(ActorRole role, int citizenId)
        {
            Role = role;
            CitizenId = citizenId;
        }

        public static readonly Actor Creator = new Actor(ActorRole.Creator, 0);
        public static Actor Resident(int citizenId) => new Actor(ActorRole.Resident, citizenId);
    }

    /// <summary>Marks commands that edit or manage the world; only the creator may run them.</summary>
    public interface ICreatorCommand : IWorldCommand
    {
    }

    /// <summary>A personal action of the controlled resident.</summary>
    public interface IResidentCommand
    {
        CommandResult Execute(WorldState world, Citizen self);
    }

    /// <summary>Single entry point that checks the actor's permission before running a command.</summary>
    public static class CommandGate
    {
        public static CommandResult Execute(WorldState world, Actor actor, object command)
        {
            switch (command)
            {
                case IResidentCommand rc:
                    if (actor.Role != ActorRole.Resident) return CommandResult.Fail("人物を操作していません");
                    if (!world.Player.IsActive || world.Player.CitizenId != actor.CitizenId)
                        return CommandResult.Fail("この人物は操作中ではありません");
                    if (!world.Economy.Citizens.TryGetValue(actor.CitizenId, out Citizen self))
                        return CommandResult.Fail("操作中の人物がいません");
                    return rc.Execute(world, self);
                case ICreatorCommand cc:
                    if (actor.Role != ActorRole.Creator) return CommandResult.Fail("人生体験モードでは世界を編集できません（世界創造モードに切り替えてください）");
                    return cc.Execute(world);
                case IWorldCommand wc:
                    return wc.Execute(world);
                default:
                    return CommandResult.Fail("不明な操作です");
            }
        }
    }
}
