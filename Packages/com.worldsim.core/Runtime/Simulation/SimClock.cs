namespace WorldSim.Core.Simulation
{
    /// <summary>
    /// Simulation time measured in integer ticks. One tick is one in-game hour; a simplified
    /// 360-day calendar keeps month/season maths trivial for later systems.
    /// </summary>
    public sealed class SimClock
    {
        public const int TicksPerDay = 24;
        public const int DaysPerYear = 360;
        public const int TicksPerYear = TicksPerDay * DaysPerYear;

        public long Tick { get; internal set; }

        public long TotalDays => Tick / TicksPerDay;
        public int HourOfDay => (int)(Tick % TicksPerDay);
        public int Year => (int)(Tick / TicksPerYear) + 1;
        public int DayOfYear => (int)(TotalDays % DaysPerYear) + 1;

        public override string ToString() => $"{Year}年目 {DayOfYear}日 {HourOfDay:00}:00";
    }
}
