namespace SteamAchievementUnlocker;

public static class RarityStyle
{
    public static bool IsRare(double? percent) => percent is double p && double.IsFinite(p) && p >= 0 && p <= 5;
    // A smooth 2.4-second brightness cycle, never an on/off flash.
    public static double Intensity(long milliseconds, bool animations) => animations ? 0.65 + 0.35 * (0.5 + 0.5 * Math.Sin(milliseconds * 2 * Math.PI / 2400d)) : 1;
}
