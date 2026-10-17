using System;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.People
{
    /// <summary>
    /// Hourly life of the controlled resident: hunger and energy, hours worked at the actual
    /// workplace during working hours, training at school, arrival after travel. Other residents'
    /// daily needs are covered by the daily systems (Retail, Finance); this only adds the
    /// hour-level detail that a person living in the world experiences.
    /// </summary>
    public sealed class AvatarSystem : ISimulationSystem
    {
        public const int WorkStartHour = 8, WorkEndHour = 18;
        public const double StudyHoursPerLevel = 160;
        public const long TuitionPerHour = 800;

        public string Name => "Avatar";
        public int IntervalTicks => 1;

        public void Tick(WorldState w)
        {
            PlayerState p = w.Player;
            if (!p.IsActive) return;
            if (!w.Economy.Citizens.TryGetValue(p.CitizenId, out Citizen c)) { p.Clear(); return; }

            if (p.Activity == AvatarActivity.Travelling && w.Clock.Tick >= p.TravelArrive) Arrive(w, p);
            if (p.InsideBuildingId != 0 && !w.TryGetBuilding(p.InsideBuildingId, out _)) LeaveBuilding(w, p);

            bool asleep = p.Activity == AvatarActivity.Sleeping;
            p.Hunger = Clamp(p.Hunger - (asleep ? 1f / 36 : 1f / 18));
            if (asleep) p.Energy = Clamp(p.Energy + 1f / 7);
            else if (p.Activity == AvatarActivity.Resting) p.Energy = Clamp(p.Energy + 1f / 16);
            else p.Energy = Clamp(p.Energy - (p.Activity == AvatarActivity.Working ? 1f / 12 : 1f / 17));
            if (asleep && p.Energy >= 1f) p.Activity = AvatarActivity.Idle;

            int hour = w.Clock.HourOfDay;
            if (p.Activity == AvatarActivity.Working)
            {
                bool atWork = c.JobId != 0 && p.InsideBuildingId == c.JobId;
                if (!atWork) p.Activity = AvatarActivity.Idle;
                else if (hour >= WorkStartHour && hour < WorkEndHour && p.Energy > 0.05f) p.WorkedHours += 1;
            }
            if (p.Activity == AvatarActivity.Studying)
            {
                long paid = w.Economy.Ledger.TransferUpTo(c, w.Economy.City, TuitionPerHour, Flow.Tuition);
                if (paid < TuitionPerHour) { p.Activity = AvatarActivity.Idle; w.Log("人生", "授業料が払えず、訓練を中断しました"); }
                else
                {
                    p.StudyHours += 1;
                    if (p.StudyHours >= StudyHoursPerLevel && c.Education < 2)
                    {
                        c.Education++;
                        p.StudyHours = 0;
                        w.Log("人生", $"{Names.Of(c.Id)}が職業訓練を修了し、学歴が上がりました");
                    }
                }
            }

            // The person's own experience feeds the same happiness model as everyone else.
            c.FoodMet = Math.Min(1f, p.Hunger / 0.4f);
            if (p.Hunger <= 0f || p.Energy <= 0f) c.Happiness = Math.Max(0f, c.Happiness - 0.01f);
            if (c.Relations != null && hour == 0)
                foreach (int k in new System.Collections.Generic.List<int>(c.Relations.Keys)) c.Relations[k] = Math.Max(0f, c.Relations[k] - 0.002f);
        }

        /// <summary>Completes travel if the arrival time has passed (also called with fractional time by the UI).</summary>
        public static bool TryArrive(WorldState w, double now)
        {
            PlayerState p = w.Player;
            if (!p.IsActive || p.Activity != AvatarActivity.Travelling || now < p.TravelArrive) return false;
            Arrive(w, p);
            return true;
        }

        private static void Arrive(WorldState w, PlayerState p)
        {
            p.Activity = AvatarActivity.Idle;
            if (w.TryGetBuilding(p.TravelTo, out Building b)) PlaceAtDoor(w, p, b);
            p.TravelTo = 0;
        }

        public static void PlaceAtDoor(WorldState w, PlayerState p, Building b)
        {
            var (x, z, nx, nz) = w.DoorOf(b);
            p.X = x + nx * 2.5f;
            p.Z = z + nz * 2.5f;
            p.Heading = (float)Math.Atan2(nx, nz);
            p.InsideBuildingId = 0;
        }

        public static void LeaveBuilding(WorldState w, PlayerState p)
        {
            if (p.InsideBuildingId != 0 && w.TryGetBuilding(p.InsideBuildingId, out Building b)) PlaceAtDoor(w, p, b);
            p.InsideBuildingId = 0;
            if (p.Activity != AvatarActivity.Travelling) p.Activity = AvatarActivity.Idle;
        }

        private static float Clamp(float v) => Math.Max(0f, Math.Min(1f, v));
    }
}
