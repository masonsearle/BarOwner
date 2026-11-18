using System.Collections.Generic;
using BarOwner.Interaction;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Decor
{
    public enum DecorType : byte
    {
        Plant = 0,
        FloorLamp = 1,
        Rug = 2,
        Jukebox = 3,
        Television = 4,
        MicStand = 5,
        Amplifier = 6,
    }

    /// <summary>Furniture catalogue: price, how much it lifts the room, and what it unlocks.</summary>
    public static class DecorCatalog
    {
        public static readonly DecorType[] All =
        {
            DecorType.Plant, DecorType.FloorLamp, DecorType.Rug, DecorType.Jukebox,
            DecorType.Television, DecorType.MicStand, DecorType.Amplifier,
        };

        public static string Name(DecorType type) => type switch
        {
            DecorType.Plant => "Potted plant",
            DecorType.FloorLamp => "Floor lamp",
            DecorType.Rug => "Rug",
            DecorType.Jukebox => "Jukebox",
            DecorType.Television => "Big telly",
            DecorType.MicStand => "Karaoke mic",
            DecorType.Amplifier => "Band amp",
            _ => "Thing",
        };

        public static int PricePence(DecorType type) => type switch
        {
            DecorType.Plant => 3000,
            DecorType.FloorLamp => 4500,
            DecorType.Rug => 3500,
            DecorType.Jukebox => 12000,
            DecorType.Television => 15000,
            DecorType.MicStand => 6000,
            DecorType.Amplifier => 8000,
            _ => 0,
        };

        /// <summary>Ambience points while it's in the bar room (the first of each kind counts fully, extras half).</summary>
        public static float Ambience(DecorType type) => type switch
        {
            DecorType.Plant => 2f,
            DecorType.FloorLamp => 3f,
            DecorType.Rug => 2f,
            DecorType.Jukebox => 4f,
            DecorType.Television => 1f,
            DecorType.MicStand => 1f,
            DecorType.Amplifier => 1f,
            _ => 0f,
        };

        public static string Unlocks(DecorType type) => type switch
        {
            DecorType.Television => "needed for Sports Night",
            DecorType.MicStand => "needed for Karaoke",
            DecorType.Amplifier => "needed for Live Music",
            _ => null,
        };
    }

    /// <summary>
    /// A piece of furniture/decor. It's an ordinary <see cref="Grabbable"/> physics object, so you place
    /// it by carrying it where you want it (Q/R to turn it while holding). Anything standing in the bar
    /// room counts towards the pub's ambience.
    /// </summary>
    [RequireComponent(typeof(Grabbable))]
    public class DecorItem : NetworkBehaviour
    {
        public static readonly List<DecorItem> All = new();

        [SerializeField] private DecorType type;

        public DecorType Type => type;

        public override void OnNetworkSpawn() => All.Add(this);
        public override void OnNetworkDespawn() => All.Remove(this);

        /// <summary>In the public bar room (not the back rooms or the street).</summary>
        public bool IsInBarRoom
        {
            get
            {
                Vector3 p = transform.position;
                return p.x > -6f && p.x < 6f && p.z > -5f && p.z < 5f && p.y > -0.5f && p.y < 3f;
            }
        }
    }
}
