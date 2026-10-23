using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Game.Session;

namespace Tzar2.Game.Ui;

/// <summary>The shield of a row: how the player stands with the kingdom (<c>SHIELD_GREEN</c>, <c>_NEUTRAL</c>, <c>_RED</c>).</summary>
public enum ChatShield
{
    /// <summary>An ally.</summary>
    Ally,

    /// <summary>Neutral.</summary>
    Neutral,

    /// <summary>An enemy.</summary>
    Enemy,
}

/// <summary>A kingdom's row on the chat screen: whether it is picked, and how the player stands with it.</summary>
/// <param name="Kingdom">The kingdom (slot).</param>
/// <param name="Shield">The player's stance towards it.</param>
/// <param name="Picked">Whether a custom message goes to it.</param>
public sealed record ChatRow(int Kingdom, ChatShield Shield, bool Picked);

/// <summary>
/// The chat screen (<c>D_CHAT</c>; HUD-011), as ReTzar reads the original's window (0x422f00): the line being written, the
/// recipients (everyone, the allies, the enemies, or the kingdoms picked from the list, which the large window shows,
/// each with the player's stance), SEND with Enter. In single player the line is where the <c>hm*</c> codes are typed.
/// </summary>
public sealed class ChatScreen
{
    /// <summary>The longest line.</summary>
    public const int MaxLength = 120;

    private readonly bool[] _picked = new bool[8];
    private readonly GameFrame _frame;
    private readonly int _me;

    /// <summary>The chat of <paramref name="me"/> on <paramref name="frame"/>, to <paramref name="to"/> (the last choice).</summary>
    public ChatScreen(GameFrame frame, int me, ChatTarget to = ChatTarget.All)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frame = frame;
        _me = me;
        To = to;
        Kingdoms = [.. Enumerable.Range(0, Math.Min(frame.Players.Length, 8)).Where(k => k != me).Take(7)];
    }

    /// <summary>The other kingdoms, by slot (seven at most, the original's rows).</summary>
    public IReadOnlyList<int> Kingdoms { get; }

    /// <summary>The line being written.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Who the line goes to.</summary>
    public ChatTarget To { get; private set; }

    /// <summary>Whether the large window (with the list of kingdoms) shows: a custom message.</summary>
    public bool Large => To == ChatTarget.Custom;

    /// <summary>The rows of the list.</summary>
    public IReadOnlyList<ChatRow> Rows => [.. Kingdoms.Select(k => new ChatRow(k, Shield(_frame.StanceOf(_me, k)), _picked[k]))];

    /// <summary>Whether kingdom <paramref name="kingdom"/> is picked.</summary>
    public bool IsPicked(int kingdom) => (uint)kingdom < 8 && _picked[kingdom];

    private static ChatShield Shield(Tzar2.Sim.Kingdoms.Stance stance) => stance switch
    {
        Tzar2.Sim.Kingdoms.Stance.Ally => ChatShield.Ally,
        Tzar2.Sim.Kingdoms.Stance.Neutral => ChatShield.Neutral,
        _ => ChatShield.Enemy,
    };

    /// <summary>Replaces the line (cut to <see cref="MaxLength"/>).</summary>
    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text.Length > MaxLength ? text[..MaxLength] : text;
    }

    /// <summary>Picks who the line goes to.</summary>
    public void Address(ChatTarget to) => To = to;

    /// <summary>Picks or unpicks a kingdom of the list.</summary>
    public void Toggle(int kingdom)
    {
        if (Kingdoms.Contains(kingdom))
        {
            _picked[kingdom] = !_picked[kingdom];
        }
    }

    /// <summary>
    /// SEND: the line, its recipients and (for <see cref="ChatTarget.Custom"/>) the mask of the picked kingdoms; null when
    /// there is nothing to send (an empty line, or a custom message to nobody).
    /// </summary>
    public (string Text, ChatTarget To, byte Mask)? Send()
    {
        string line = Text.Trim();
        byte mask = 0;
        for (int k = 0; k < 8; k++)
        {
            mask |= _picked[k] ? (byte)(1 << k) : (byte)0;
        }

        if (line.Length == 0 || (To == ChatTarget.Custom && mask == 0))
        {
            return null;
        }

        return (line, To, To == ChatTarget.Custom ? mask : (byte)0);
    }
}
