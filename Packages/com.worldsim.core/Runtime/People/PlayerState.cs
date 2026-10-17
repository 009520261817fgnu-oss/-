using System;

namespace WorldSim.Core.People
{
    public enum AvatarActivity
    {
        Idle,
        Sleeping,
        Resting,
        Working,
        Studying,
        Travelling,
    }

    /// <summary>
    /// The resident the player currently lives as (life mode). Part of the saved world state: the
    /// person keeps their place, needs and plans across save/load. CitizenId 0 means nobody.
    /// Everything else about the person (home, job, money, education) lives on the Citizen itself,
    /// so the same rules apply as for every other resident.
    /// </summary>
    public sealed class PlayerState
    {
        public const float MaxStepMeters = 30f;

        public int CitizenId;
        /// <summary>Outdoor position in world metres (meaningful while not inside a building).</summary>
        public float X, Z, Heading;
        /// <summary>Building the person is inside, 0 = outdoors.</summary>
        public int InsideBuildingId;
        /// <summary>Position inside the room, metres from the room's corner.</summary>
        public float RoomX, RoomZ;
        public AvatarActivity Activity;
        /// <summary>1 = full / rested, 0 = starving / exhausted.</summary>
        public float Hunger = 0.8f, Energy = 0.8f;
        /// <summary>Food units at home, bought at shops.</summary>
        public double Pantry;
        public double GoodsOwned;
        /// <summary>Hours worked since the last payday (FinanceSystem resets it).</summary>
        public float WorkedHours;
        /// <summary>Consecutive workdays missed.</summary>
        public int AbsenceDays;
        public double StudyHours;
        /// <summary>Destination building and arrival time (in ticks, fractional) while travelling.</summary>
        public int TravelTo;
        public double TravelArrive;

        public bool IsActive => CitizenId != 0;

        public void Clear()
        {
            CitizenId = 0;
            InsideBuildingId = 0;
            Activity = AvatarActivity.Idle;
            TravelTo = 0;
        }
    }

    /// <summary>Deterministic Japanese names derived from a resident's id (nothing extra to save).</summary>
    public static class Names
    {
        private static readonly string[] Family =
        {
            "佐藤", "鈴木", "高橋", "田中", "伊藤", "渡辺", "山本", "中村", "小林", "加藤", "吉田", "山田", "佐々木", "山口", "松本",
            "井上", "木村", "林", "斎藤", "清水", "山崎", "森", "池田", "橋本", "阿部", "石川", "山下", "中島", "石井", "小川",
        };

        private static readonly string[] Given =
        {
            "翔", "陽菜", "蓮", "結衣", "大翔", "葵", "悠真", "凛", "湊", "芽依", "樹", "さくら", "颯太", "美咲", "健太", "七海",
            "拓海", "彩花", "優斗", "真央", "誠", "千尋", "亮", "遥", "直樹", "瞳", "隆", "恵", "浩二", "由美", "修", "智子",
            "和也", "裕子", "健一", "久美子", "正人", "明美", "博", "幸子",
        };

        public static string Of(int citizenId)
        {
            ulong h = DeterministicRandom.SplitMix((ulong)citizenId * 0x9E3779B97F4A7C15UL);
            return Family[(int)(h % (ulong)Family.Length)] + " " + Given[(int)((h >> 20) % (ulong)Given.Length)];
        }
    }
}
