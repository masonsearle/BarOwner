using UnityEngine;

namespace BarOwner.Bar
{
    /// <summary>What's in a glass. Byte-sized so it's cheap to sync.</summary>
    public enum DrinkType : byte
    {
        None = 0,
        Lager = 1,
        Bitter = 2,
        Cider = 3,
        Slops = 255, // two drinks mixed in one glass. Nobody's paying for that.
    }

    /// <summary>Static drink catalogue. Becomes data assets + player-set prices in M5.</summary>
    public static class Drinks
    {
        public static readonly DrinkType[] OnTap = { DrinkType.Lager, DrinkType.Bitter, DrinkType.Cider };

        public static string Name(DrinkType type) => type switch
        {
            DrinkType.Lager => "Lager",
            DrinkType.Bitter => "Bitter",
            DrinkType.Cider => "Cider",
            DrinkType.Slops => "Slops",
            _ => "Nothing",
        };

        public static Color LiquidColour(DrinkType type) => type switch
        {
            DrinkType.Lager => new Color(0.98f, 0.72f, 0.12f, 0.92f),
            DrinkType.Bitter => new Color(0.55f, 0.26f, 0.08f, 0.95f),
            DrinkType.Cider => new Color(0.95f, 0.85f, 0.4f, 0.9f),
            DrinkType.Slops => new Color(0.4f, 0.33f, 0.2f, 0.95f),
            _ => Color.clear,
        };

        /// <summary>Thickness of the foam head as a fraction of glass height.</summary>
        public static float HeadAmount(DrinkType type) => type switch
        {
            DrinkType.Lager => 0.06f,
            DrinkType.Bitter => 0.05f,
            DrinkType.Cider => 0.015f,
            _ => 0f,
        };

        /// <summary>Price of a pint in pence.</summary>
        public static int PricePence(DrinkType type) => type switch
        {
            DrinkType.Lager => 450,
            DrinkType.Bitter => 420,
            DrinkType.Cider => 480,
            _ => 0,
        };
    }

    public static class Money
    {
        /// <summary>1234 → "£12.34"; negative values get a minus sign.</summary>
        public static string Format(int pence)
        {
            string sign = pence < 0 ? "-" : "";
            pence = Mathf.Abs(pence);
            return $"{sign}£{pence / 100}.{pence % 100:00}";
        }
    }
}
