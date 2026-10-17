using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using WorldSim.Core.Bootstrap;
using WorldSim.Core.Persistence;
using WorldSim.Core.Simulation;
using WorldSim.Core.World;

namespace WorldSim.Core.Tests
{
    /// <summary>
    /// S1: causal probes. The default town is warmed up for 60 days and saved; every intervention
    /// then starts from that same save and runs 90 days. Each test pins the expected DIRECTION of
    /// an effect, so a change that silently cuts a causal link fails here.
    /// </summary>
    public class CausalProbeTests
    {
        private const int WarmUpDays = 60, ProbeDays = 90;

        private sealed class Probe
        {
            public ProbeResult Baseline;
            public Dictionary<string, ProbeResult> Results = new Dictionary<string, ProbeResult>();
            public string Save;
        }

        private static readonly Lazy<Probe> Shared = new Lazy<Probe>(() =>
        {
            var w = WorldFactory.Create(TestContent.Load(), new WorldGenOptions { Seed = 1 });
            WorldFactory.CreateRunner(w).StepMany(WarmUpDays * SimClock.TicksPerDay);
            var p = new Probe { Save = SaveSystem.Save(w) };
            p.Baseline = CausalProbe.Run(TestContent.Load(), p.Save, null, ProbeDays);
            foreach (Intervention iv in CausalProbe.Standard())
                p.Results[iv.Name] = CausalProbe.Run(TestContent.Load(), p.Save, iv, ProbeDays);
            return p;
        });

        /// <summary>Mean relative change; also requires that every noise seed agrees (a real effect, not chance).</summary>
        private static double Change(string intervention, string key)
        {
            Probe p = Shared.Value;
            ProbeResult r = p.Results[intervention];
            Assert.IsTrue(r.Consistent(p.Baseline, key), $"{intervention} → {key} depends on chance");
            return r.Change(p.Baseline, key);
        }

        [Test]
        public void ProbesAreDeterministic()
        {
            Probe p = Shared.Value;
            ProbeResult again = CausalProbe.Run(TestContent.Load(), p.Save, null, ProbeDays);
            foreach (string k in CausalProbe.Series)
            {
                Assert.AreEqual(p.Baseline.Values[k], again.Values[k], k);
                Assert.IsTrue(p.Baseline.Consistent(p.Baseline, k));
            }
        }

        [Test]
        public void ChanceEventsAreNotMistakenForEffects()
        {
            // Pension cuts do not touch the power plant; a blackout that appears under one noise seed
            // only (a random breakdown) must be reported as chance.
            Probe p = Shared.Value;
            ProbeResult pension = p.Results["年金 0"];
            if (Math.Abs(pension.Change(p.Baseline, "energySupply")) >= 0.01)
                Assert.IsFalse(pension.Consistent(p.Baseline, "energySupply"));
            Assert.AreEqual(CausalProbe.DefaultNoise.Length, p.Baseline.Runs.Count);
        }

        [Test]
        public void ExpensiveElectricityBankruptsEnergyHungryFoodPlants()
        {
            Assert.Less(Change("電気料金 100円", "prod.food"), -0.9);
            Assert.Greater(Change("電気料金 100円", "imports"), 0.1, "food is imported instead");
            Assert.Greater(Change("電気料金 100円", "unemployment"), 0.1);
        }

        [Test]
        public void IncomeTaxFillsTheTreasuryAndCutsSpending()
        {
            Assert.Greater(Change("所得税 40%", "treasury"), 0.05);
            Assert.Less(Change("所得税 40%", "retailSales"), 0);
        }

        [Test]
        public void CuttingUnemploymentBenefitSavesMoneyButHurtsDemand()
        {
            Assert.Greater(Change("失業手当 0", "treasury"), 0);
            Assert.Less(Change("失業手当 0", "retailSales"), 0);
            Assert.Less(Change("失業手当 0", "happiness"), 0);
        }

        [Test]
        public void IndustrySubsidyCreatesJobsAtTheTreasurysExpense()
        {
            Assert.Less(Change("産業補助 2万円", "unemployment"), 0);
            Assert.Less(Change("産業補助 2万円", "treasury"), 0);
            Assert.Greater(Change("産業補助 2万円", "prod.goods"), 0);
        }

        [Test]
        public void WithoutPowerTheEconomyStops()
        {
            Assert.AreEqual(-1.0, Change("発電所をすべて撤去", "energySupply"), 1e-9);
            Assert.Greater(Change("発電所をすべて撤去", "unemployment"), 0.2);
            Assert.Less(Change("発電所をすべて撤去", "happiness"), -0.1);
            Assert.Less(Change("発電所をすべて撤去", "prod.goods"), -0.5);
        }

        [Test]
        public void WithoutShopsHouseholdsCannotBuyAndPeopleLeave()
        {
            Assert.Less(Change("商店をすべて撤去", "retailSales"), -0.5);
            Assert.Less(Change("商店をすべて撤去", "happiness"), 0);
            Assert.Less(Change("商店をすべて撤去", "population"), 0);
        }

        [Test]
        public void MissingLocalFoodIsImported()
        {
            Assert.AreEqual(-1.0, Change("食品工場を撤去", "prod.food"), 1e-9);
            Assert.Greater(Change("食品工場を撤去", "imports"), 0);
        }

        [Test]
        public void RemovingFarmsCutsExportsAndJobs()
        {
            Assert.Less(Change("農場を撤去", "exports"), 0);
            Assert.Greater(Change("農場を撤去", "unemployment"), 0);
        }

        [Test]
        public void WithoutLocalBuildersUpkeepMoneyLeavesTown()
        {
            // The city's upkeep and repair money goes to outside firms instead of local wages.
            Assert.Less(Change("建設会社を撤去", "companyProfit"), 0);
            Assert.Less(Change("建設会社を撤去", "treasury"), 0, "less local income, less tax");
            Assert.Less(Change("建設会社を撤去", "population"), 0);
        }

        [Test]
        public void HighRentFillsTheTreasuryButSqueezesHouseholds()
        {
            Assert.Greater(Change("家賃 3倍", "treasury"), 0.05);
            Assert.Less(Change("家賃 3倍", "retailSales"), 0);
            Assert.Less(Change("家賃 3倍", "happiness"), 0);
        }

        [Test]
        public void ResearchNeedsTheLab()
        {
            Assert.AreEqual(-1.0, Change("研究所と学校を撤去", "research"), 1e-9);
        }

        [Test]
        public void CuttingTheOutsideRoadStopsTrade()
        {
            Assert.AreEqual(-1.0, Change("市外への道路を遮断", "imports"), 1e-9);
            Assert.AreEqual(-1.0, Change("市外への道路を遮断", "exports"), 1e-9);
            Assert.Greater(Change("市外への道路を遮断", "unemployment"), 0.2);
        }

        [Test]
        public void EarthquakesCostTheCityAndUpsetResidents()
        {
            Assert.Less(Change("地震 M7.5", "treasury"), -0.1);
            Assert.Less(Change("地震 M7.5", "happiness"), 0);
        }

        [Test]
        public void NewHousingCostsMoneyAndNeverShrinksTheTown()
        {
            Assert.Less(Change("町の近くに集合住宅6棟", "treasury"), 0);
            Assert.GreaterOrEqual(Change("町の近くに集合住宅6棟", "population"), 0);
        }

        [Test]
        public void EveryInterventionChangesSomething()
        {
            Probe p = Shared.Value;
            foreach (var kv in p.Results)
                Assert.IsTrue(CausalProbe.Series.Any(k => Math.Abs(kv.Value.Change(p.Baseline, k)) > 0.01), kv.Key + " had no measurable effect");
        }
    }
}
