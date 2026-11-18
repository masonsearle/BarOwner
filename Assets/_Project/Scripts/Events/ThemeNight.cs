using BarOwner.Decor;

namespace BarOwner.Events
{
    public enum ThemeNight : byte
    {
        None = 0,
        Quiz = 1,
        Karaoke = 2,
        LiveMusic = 3,
        Sports = 4,
    }

    /// <summary>What each theme night does. All multipliers are relative to a normal night.</summary>
    public static class ThemeNights
    {
        public const int Count = 5;

        public static string Name(ThemeNight t) => t switch
        {
            ThemeNight.Quiz => "Quiz Night",
            ThemeNight.Karaoke => "Karaoke",
            ThemeNight.LiveMusic => "Live Music",
            ThemeNight.Sports => "Sports Night",
            _ => "Normal night",
        };

        /// <summary>Decor that has to be in the bar room for the night to go ahead.</summary>
        public static DecorType? Requires(ThemeNight t) => t switch
        {
            ThemeNight.Karaoke => DecorType.MicStand,
            ThemeNight.LiveMusic => DecorType.Amplifier,
            ThemeNight.Sports => DecorType.Television,
            _ => null,
        };

        /// <summary>Paid when the pub opens (quiz prizes, the band's fee...).</summary>
        public static int CostPence(ThemeNight t) => t switch
        {
            ThemeNight.Quiz => 2000,
            ThemeNight.LiveMusic => 8000,
            ThemeNight.Sports => 1000,
            _ => 0,
        };

        /// <summary>Lower = customers arrive more often.</summary>
        public static float ArrivalIntervalMultiplier(ThemeNight t) => t switch
        {
            ThemeNight.Quiz => 0.75f,
            ThemeNight.Karaoke => 0.7f,
            ThemeNight.LiveMusic => 0.55f,
            ThemeNight.Sports => 0.6f,
            _ => 1f,
        };

        public static float PatienceMultiplier(ThemeNight t) => t switch
        {
            ThemeNight.Quiz => 1.25f,
            ThemeNight.LiveMusic => 0.9f,
            ThemeNight.Sports => 0.8f,
            _ => 1f,
        };

        public static float DrinkTimeMultiplier(ThemeNight t) => t switch
        {
            ThemeNight.Quiz => 1.5f,
            ThemeNight.LiveMusic => 1.2f,
            _ => 1f,
        };

        public static float TipBonus(ThemeNight t) => t switch
        {
            ThemeNight.Karaoke => 0.1f,
            ThemeNight.LiveMusic => 0.05f,
            _ => 0f,
        };

        /// <summary>Order weights for lager, bitter, cider.</summary>
        public static float[] DrinkWeights(ThemeNight t) => t switch
        {
            ThemeNight.Quiz => new[] { 0.3f, 0.5f, 0.2f },
            ThemeNight.Karaoke => new[] { 0.3f, 0.1f, 0.6f },
            ThemeNight.LiveMusic => new[] { 0.45f, 0.3f, 0.25f },
            ThemeNight.Sports => new[] { 0.7f, 0.2f, 0.1f },
            _ => new[] { 1f / 3f, 1f / 3f, 1f / 3f },
        };
    }
}
