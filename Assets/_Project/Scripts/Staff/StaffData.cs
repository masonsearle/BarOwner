using System;
using Unity.Collections;
using Unity.Netcode;

namespace BarOwner.Staff
{
    /// <summary>
    /// One applicant or employee. Stats run 1–5:
    ///  Speed   → walks and pours faster.
    ///  Charm   → more tips.
    ///  Honesty → how much of the takings actually reach the till.
    /// Rota is a 7-bit mask (bit 0 = Monday).
    /// </summary>
    public struct StaffData : INetworkSerializable, IEquatable<StaffData>
    {
        public int Id;
        public FixedString32Bytes Name;
        public byte Speed;
        public byte Charm;
        public byte Honesty;
        public int WagePence; // per shift
        public byte Rota;

        public bool WorksOn(int weekday) => (Rota & (1 << weekday)) != 0;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Id);
            serializer.SerializeValue(ref Name);
            serializer.SerializeValue(ref Speed);
            serializer.SerializeValue(ref Charm);
            serializer.SerializeValue(ref Honesty);
            serializer.SerializeValue(ref WagePence);
            serializer.SerializeValue(ref Rota);
        }

        public bool Equals(StaffData other) =>
            Id == other.Id && Name.Equals(other.Name) && Speed == other.Speed && Charm == other.Charm &&
            Honesty == other.Honesty && WagePence == other.WagePence && Rota == other.Rota;

        public override bool Equals(object obj) => obj is StaffData other && Equals(other);
        public override int GetHashCode() => Id;
    }

    public static class StaffNames
    {
        public static readonly string[] Weekdays = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        private static readonly string[] First =
        {
            "Dave", "Sharon", "Kev", "Tracy", "Gaz", "Bev", "Lee", "Shaz", "Dean", "Jo", "Wayne", "Kerry",
            "Ian", "Mandy", "Craig", "Leanne", "Phil", "Stacey", "Rob", "Gemma", "Terry", "Paula", "Mo", "Priya",
        };

        private static readonly string[] Last =
        {
            "Smith", "Jones", "Taylor", "Brown", "Wilson", "Evans", "Walker", "Wright", "Hughes", "Green",
            "Hall", "Wood", "Clarke", "Patel", "Khan", "Murphy", "Doyle", "Baker", "Turner", "Cooper",
        };

        /// <summary>Weekday for a game day (day 1 = Monday).</summary>
        public static int WeekdayOf(int day) => ((day - 1) % 7 + 7) % 7;

        public static StaffData RandomApplicant(Random rng, int id)
        {
            byte Stat() => (byte)rng.Next(1, 6);
            var data = new StaffData
            {
                Id = id,
                Name = new FixedString32Bytes($"{First[rng.Next(First.Length)]} {Last[rng.Next(Last.Length)]}"),
                Speed = Stat(),
                Charm = Stat(),
                Honesty = Stat(),
                Rota = 0b1111111,
            };
            // Better staff cost more: £20 base + £4 per stat point above 1, ± a bit of haggling.
            int points = data.Speed + data.Charm + data.Honesty - 3;
            data.WagePence = 2000 + points * 400 + rng.Next(-2, 3) * 100;
            return data;
        }
    }
}
