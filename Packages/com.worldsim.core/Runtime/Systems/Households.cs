using System;
using System.Collections.Generic;
using WorldSim.Core.Economy;
using WorldSim.Core.World;

namespace WorldSim.Core.Systems
{
    /// <summary>
    /// Households (S4): couples form among single adults who live in the same building, share a home,
    /// have children, and move or leave town together; young adults eventually leave their parents'
    /// home. A household is the person, their partner and their children under 18 living with them.
    /// </summary>
    public static class Households
    {
        public const int MinCoupleAge = 20, MaxCoupleAge = 50, MaxAgeGap = 10;
        public const double CoupleChancePerDay = 0.003;
        public const double CoupleBirthRatePerYear = 0.15;
        public const double SeparationChancePerDay = 0.0005;
        public const double LeaveHomeChancePerDay = 0.004;

        /// <summary>The person, their partner and dependent children living with them.</summary>
        public static List<Citizen> Members(EconomyState e, Citizen c, long today)
        {
            var list = new List<Citizen> { c };
            if (c.PartnerId != 0 && e.Citizens.TryGetValue(c.PartnerId, out Citizen p) && p.HomeId == c.HomeId) list.Add(p);
            foreach (Citizen k in e.Citizens.Values)
                if (k.ParentId != 0 && k.AgeYears(today) < 18 && k.HomeId == c.HomeId && k.Id != c.Id
                    && (k.ParentId == c.Id || k.ParentId == c.PartnerId) && !list.Contains(k)) list.Add(k);
            return list;
        }

        /// <summary>Moves the whole household into <paramref name="homeId"/> if there is room for all of them.</summary>
        public static bool MoveTogether(WorldState w, Citizen c, int homeId, long today)
        {
            EconomyState e = w.Economy;
            if (!w.TryGetBuilding(homeId, out Building b)) return false;
            List<Citizen> members = Members(e, c, today);
            int already = 0;
            foreach (Citizen m in members) if (m.HomeId == homeId) already++;
            if (e.Residents(homeId) - already + members.Count > b.Def.Housing) return false;
            foreach (Citizen m in members) if (m.HomeId != homeId) e.MoveIn(m, homeId);
            return true;
        }

        public static void Marry(EconomyState e, Citizen a, Citizen b)
        {
            a.PartnerId = b.Id;
            b.PartnerId = a.Id;
            e.Causes.Note(a.Id, Cause.Married, b.Id);
            e.Causes.Note(b.Id, Cause.Married, a.Id);
        }

        public static void Separate(EconomyState e, Citizen a)
        {
            if (a.PartnerId != 0 && e.Citizens.TryGetValue(a.PartnerId, out Citizen b) && b.PartnerId == a.Id) b.PartnerId = 0;
            a.PartnerId = 0;
        }

        /// <summary>Daily: new couples among singles sharing a building, and the occasional separation.</summary>
        public static void Update(WorldState w, long today, DeterministicRandom rng, int player)
        {
            EconomyState e = w.Economy;
            var singles = new SortedDictionary<int, List<Citizen>>();
            foreach (Citizen c in e.Citizens.Values)
            {
                if (c.HomeId == 0 || c.Id == player) continue;
                int age = c.AgeYears(today);
                if (c.PartnerId != 0)
                {
                    // Unhappy couples sometimes split; one of them moves out.
                    if (c.Id < c.PartnerId && c.Happiness < 0.35f && rng.Chance(SeparationChancePerDay)
                        && e.Citizens.TryGetValue(c.PartnerId, out Citizen p) && p.Id != player && p.Happiness < 0.35f)
                    {
                        Separate(e, c);
                        e.Causes.Note(c.Id, Cause.Separated, p.Id);
                        e.Causes.Note(p.Id, Cause.Separated, c.Id);
                        e.MoveIn(p, 0); // finds a new home (or leaves) through the homeless rules
                    }
                    continue;
                }
                if (age < MinCoupleAge || age > MaxCoupleAge) continue;
                if (!singles.TryGetValue(c.HomeId, out List<Citizen> list)) singles[c.HomeId] = list = new List<Citizen>();
                list.Add(c);
            }
            foreach (var kv in singles)
            {
                List<Citizen> list = kv.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    Citizen a = list[i];
                    if (a.PartnerId != 0 || !rng.Chance(CoupleChancePerDay)) continue;
                    int ageA = a.AgeYears(today);
                    for (int j = 0; j < list.Count; j++)
                    {
                        Citizen b = list[j];
                        if (b == a || b.PartnerId != 0 || Math.Abs(b.AgeYears(today) - ageA) > MaxAgeGap) continue;
                        Marry(e, a, b);
                        break;
                    }
                }
            }
        }

        /// <summary>Daily: grown-up children (20–30) living with a parent move to a home of their own.</summary>
        public static void LeaveParents(WorldState w, long today, DeterministicRandom rng, List<Building> homes, Func<Citizen, int, bool> findHomeExcept)
        {
            EconomyState e = w.Economy;
            var movers = new List<Citizen>();
            foreach (Citizen c in e.Citizens.Values)
            {
                if (c.ParentId == 0 || c.HomeId == 0 || c.Id == w.Player.CitizenId) continue;
                int age = c.AgeYears(today);
                if (age < 20 || age > 30) continue;
                if (!e.Citizens.TryGetValue(c.ParentId, out Citizen parent) || parent.HomeId != c.HomeId) continue;
                if (rng.Chance(LeaveHomeChancePerDay)) movers.Add(c);
            }
            foreach (Citizen c in movers)
                if (findHomeExcept(c, c.HomeId)) e.Causes.Note(c.Id, Cause.LeftParentsHome, c.HomeId);
        }
    }
}
