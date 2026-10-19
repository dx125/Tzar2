using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Tzar2.Game.Multiplayer;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// <c>--mp-smoke</c> (ADR 0168): drives the real multiplayer screens headlessly. It hosts a LAN game (embedded relay),
/// fills slot 2 with a Hard computer and starts the match. It plays <c>--ticks</c> ticks through the lockstep with a
/// chat line, leaves, then opens the recorded replay in the viewer, plays it at 16x, seeks back and checks its hashes.
/// It prints <c>MP SMOKE OK</c> and quits with exit code 0 on success.
/// </summary>
public partial class GameApp
{
    private async Task MultiplayerSmokeAsync()
    {
        int exit = 1;
        try
        {
            ShowMultiplayer();
            _lanHost = LanHost.Start(relayPort: 0, discoveryPort: 0, desyncDirectory: UserPath("desync"));
            OpenLobby(() => NetworkMatch.Create(NetworkOptions("127.0.0.1", _lanHost.Port), "smoke", 2, isPublic: true));
            NetworkMatch match = _network ?? throw new InvalidOperationException("The lobby did not open.");
            await Until(() => match.Lobby is not null, 10, "lobby");
            match.SetSettings(new MatchSettings { Map = MapSourceKind.TestMap, TestMapSize = 48, Seed = 5, SpeedPercent = 300 });
            match.SetSlot(1, SlotKind.Computer, new SlotChoice("euro", 0, Difficulty.Hard));
            await Until(() => match.Lobby!.Slots[1].Kind == SlotKind.Computer && match.Lobby.Settings.TestMapSize == 48, 10, "computer slot");
            match.Start(new MatchRules());
            await Until(() => _networkGameShown && match.Stage == LobbyStage.Running, 30, "match start");
            GameSession session = match.Session!;
            session.SubmitChat("smoke chat");
            await Until(() => session.TicksRun >= _args.Ticks, 120, $"{_args.Ticks} ticks");
            if (match.Desynced || match.EndReason is not null)
            {
                throw new InvalidOperationException("The match ended: " + match.EndReason);
            }

            int played = session.TicksRun;
            SaveReplay(session, "mp");
            string replay = Directory.GetFiles(UserPath("replays"), "mp-*.t2rep").OrderByDescending(File.GetLastWriteTimeUtc).First();
            PlayReplay(replay);
            ReplayPlayback playback = _playback ?? throw new InvalidOperationException("The replay did not open.");
            for (int i = 0; i < 4; i++)
            {
                playback.ChangeSpeed(+1);
            }

            await Until(() => playback.Tick >= Math.Min(played, playback.Length) - 1, 60, "replay to the end");
            playback.Seek(played / 2);
            await Until(() => _playbackGeneration == playback.Generation && playback.Tick >= played / 2, 30, "backward seek");
            if (playback.MismatchTick >= 0)
            {
                throw new InvalidOperationException($"The replay desynced at tick {playback.MismatchTick}.");
            }

            GD.Print($"MP SMOKE OK ticks={played} replay={playback.Length} generation={playback.Generation} delay={match.InputDelay}");
            exit = 0;
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException or System.Net.Sockets.SocketException)
        {
            GD.PrintErr("MP SMOKE FAILED: " + e.Message);
        }

        LeaveNetwork();
        if (_current is not null)
        {
            RemoveChild(_current);
            _current.QueueFree();
            _current = null;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetTree().Quit(exit);
    }

    private async Task Until(Func<bool> done, int seconds, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed.TotalSeconds > seconds)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }
}
