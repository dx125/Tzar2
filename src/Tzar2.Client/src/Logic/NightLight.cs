namespace Tzar2.Client.Logic;

/// <summary>
/// The night as the original's (PRES-015; ReTzar's reading, src/simulation.rs night_amount and lighting): the whole map is
/// tinted toward blue, red to 55 % and green to 68 % of its colour at full night, from 22:00 to 06:00, with half an hour of
/// dusk before and of dawn after, in steps of three game minutes. No light falls round the units: the night lights of
/// ReTzar are its own, and Tzar2's light discs of three tiles hid what the units saw (the eighth play-test).
/// </summary>
public static class NightLight
{
    /// <summary>How deep the night is at <paramref name="dayMinute"/> (0 = midnight): 0 by day, 1 at full night.</summary>
    public static float Amount(int dayMinute)
    {
        int step = (((dayMinute % 1440) + 1440) % 1440) / 3;
        return step switch
        {
            >= 130 and < 430 => 0f,
            >= 120 and < 130 => (130 - step) / 10f,
            >= 430 and < 440 => (step - 430) / 10f,
            _ => 1f,
        };
    }

    /// <summary>What the map's colours are multiplied by at night amount <paramref name="amount"/>.</summary>
    public static (float R, float G, float B) Tint(float amount) => (1f - (0.45f * amount), 1f - (0.32f * amount), 1f);
}
