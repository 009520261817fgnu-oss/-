namespace WorldSim.Core.Transport
{
    /// <summary>
    /// A day's worth of vehicles travelling between two buildings (or a building and the map edge
    /// when <see cref="ToBuilding"/> is 0). Collected by economic systems, assigned to roads by TrafficSystem.
    /// </summary>
    public struct Trip
    {
        public int FromBuilding;
        public int ToBuilding;
        public float Vehicles;
        public bool Freight;

        public Trip(int from, int to, float vehicles, bool freight)
        {
            FromBuilding = from;
            ToBuilding = to;
            Vehicles = vehicles;
            Freight = freight;
        }
    }
}
