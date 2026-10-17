namespace WorldSim.Core.World
{
    public enum WorldChangeKind
    {
        Terrain,
        Buildings,
        Roads,
        Vegetation,
    }

    /// <summary>Notification that a region of the world changed; presentation rebuilds only that region.</summary>
    public readonly struct WorldChange
    {
        public readonly WorldChangeKind Kind;
        public readonly GridRect Region;

        public WorldChange(WorldChangeKind kind, GridRect region)
        {
            Kind = kind;
            Region = region;
        }

        public override string ToString() => $"{Kind} {Region}";
    }

    public readonly struct CommandResult
    {
        public readonly bool Success;
        public readonly string Message;
        /// <summary>Id of the entity created, if any.</summary>
        public readonly int CreatedId;

        private CommandResult(bool success, string message, int createdId)
        {
            Success = success;
            Message = message;
            CreatedId = createdId;
        }

        public static CommandResult Ok(string message = "", int createdId = 0) => new CommandResult(true, message, createdId);
        public static CommandResult Fail(string message) => new CommandResult(false, message, 0);
        public override string ToString() => (Success ? "OK" : "FAIL") + (string.IsNullOrEmpty(Message) ? "" : ": " + Message);
    }
}
