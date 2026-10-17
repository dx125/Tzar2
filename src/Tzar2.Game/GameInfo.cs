using System.Reflection;

namespace Tzar2.Game;

/// <summary>Engine identity shown in the client and written into replays and saves later (REQ-NET-09).</summary>
public static class GameInfo
{
    /// <summary>The codename (the public product name is not decided, TDD Q-01).</summary>
    public const string Codename = "Tzar2";

    /// <summary>The engine version from the assembly's informational version.</summary>
    public static string Version { get; } =
        typeof(GameInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
