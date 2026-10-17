namespace WorldSim.Core.Environment
{
    /// <summary>Weather, pollution, energy balance and active hazards.</summary>
    public sealed class EnvironmentState
    {
        /// <summary>World cells per pollution grid cell along each axis.</summary>
        public const int PollutionCell = 4;

        public float Temperature = 15f;
        /// <summary>Rain today in mm.</summary>
        public float RainToday;
        /// <summary>0..1 soil moisture; drives crop yields and flood risk.</summary>
        public float SoilMoisture = 0.5f;
        public float RainLast3Days;
        public int WetStreak;
        /// <summary>Accumulated warming in °C caused by cumulative emissions (very simplified climate).</summary>
        public double Warming;
        public double CumulativeEmissions;

        public readonly int GridW, GridD;
        /// <summary>Pollution concentration per coarse cell (arbitrary units, ~0..100).</summary>
        public readonly float[] Pollution;

        public double EnergySupply, EnergyDemand;
        /// <summary>Fraction of demand that could be served yesterday (0..1).</summary>
        public double EnergyRatio = 1;

        /// <summary>Absolute water surface height while a flood is active, otherwise 0.</summary>
        public float FloodLevel;
        public int FloodDaysLeft;

        public EnvironmentState(int worldWidth, int worldDepth)
        {
            GridW = (worldWidth + PollutionCell - 1) / PollutionCell;
            GridD = (worldDepth + PollutionCell - 1) / PollutionCell;
            Pollution = new float[GridW * GridD];
        }

        public float PollutionAt(GridPos cell)
        {
            int gx = cell.X / PollutionCell, gz = cell.Z / PollutionCell;
            if (gx < 0 || gz < 0 || gx >= GridW || gz >= GridD) return 0;
            return Pollution[gz * GridW + gx];
        }

        public float AveragePollution()
        {
            double s = 0;
            foreach (float p in Pollution) s += p;
            return (float)(s / Pollution.Length);
        }
    }
}
