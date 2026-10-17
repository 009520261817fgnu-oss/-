using System;
using System.Collections.Generic;
using WorldSim.Core.Economy;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.People
{
    public enum ScheduleActivity
    {
        Home,
        Commute,
        Work,
        Shopping,
        School,
    }

    /// <summary>
    /// Where a resident is during each hour, derived from their age, job, home and the weekday.
    /// The daily economy is computed per day, so this is the hour-by-hour picture of the same life:
    /// who is at home, at work, at school or in a shop right now. Used for the people shown inside
    /// buildings, commuters on the roads, and whom the player can talk to.
    /// </summary>
    public static class DailySchedule
    {
        public static bool IsWorkday(long day) => day % 7 < 5;

        /// <summary>Activity and the building where it happens (0 while commuting).</summary>
        public static (ScheduleActivity activity, int buildingId) At(WorldState w, Citizen c, int hour, long day)
        {
            int age = c.AgeYears(day);
            int home = c.HomeId;
            if (age >= 6 && age < 18 && hour >= 8 && hour < 15 && IsWorkday(day))
            {
                int school = NearestOfKind(w, home, "students");
                if (school != 0) return (ScheduleActivity.School, school);
            }
            if (c.JobId != 0 && IsWorkday(day))
            {
                if (hour == 7) return (ScheduleActivity.Commute, 0);
                if (hour >= 8 && hour < 17) return (ScheduleActivity.Work, c.JobId);
                if (hour == 17) return (ScheduleActivity.Commute, 0);
            }
            if (age >= 18 && (IsWorkday(day) ? hour == 18 : hour >= 10 && hour < 13))
            {
                int shop = NearestRetail(w, home);
                if (shop != 0) return (ScheduleActivity.Shopping, shop);
            }
            return (ScheduleActivity.Home, home);
        }

        /// <summary>Residents (other than <paramref name="except"/>) scheduled to be inside a building now.</summary>
        public static List<Citizen> Occupants(WorldState w, int buildingId, int except = 0)
        {
            var list = new List<Citizen>();
            int hour = w.Clock.HourOfDay;
            long day = w.Clock.TotalDays;
            foreach (Citizen c in w.Economy.Citizens.Values)
            {
                if (c.Id == except || c.Id == w.Player.CitizenId) continue;
                if (At(w, c, hour, day).buildingId == buildingId) list.Add(c);
            }
            return list;
        }

        private static readonly Dictionary<long, int> Cache = new Dictionary<long, int>();
        private static int _cacheVersion = -1;
        private static WorldState _cacheWorld;

        private static int NearestRetail(WorldState w, int home) => Nearest(w, home, 1, co => co.IsRetail && !co.Closed);

        private static int NearestOfKind(WorldState w, int home, string param) =>
            Nearest(w, home, 2, co => co.Def.GetParam(param) > 0 && !co.Closed);

        private static int Nearest(WorldState w, int home, int kind, Func<Company, bool> match)
        {
            if (home == 0) return 0;
            if (_cacheWorld != w || _cacheVersion != w.Network.Version)
            {
                Cache.Clear();
                _cacheWorld = w;
                _cacheVersion = w.Network.Version;
            }
            long key = ((long)home << 4) | (uint)kind;
            if (Cache.TryGetValue(key, out int id)) return id;
            float best = float.PositiveInfinity;
            foreach (Company co in w.Economy.Companies.Values)
            {
                if (!match(co)) continue;
                float h = w.Network.TravelHours(home, co.BuildingId);
                if (h < best) { best = h; id = co.BuildingId; }
            }
            Cache[key] = id;
            return id;
        }
    }
}
