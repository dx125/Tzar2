using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Tzar2.Net.Lan;
using Tzar2.Net.Protocol;
using Tzar2.Net.Relay;
using Xunit;

namespace Tzar2.Net.Tests;

[Collection("network")]
public sealed class LanAndLimitTests
{
    [Fact]
    public void A_browser_finds_a_host_on_this_machine()
    {
        int port = 47_000 + Random.Shared.Next(2_000);
        var lobby = new LobbySummary("ABCDEF", "Living room", 1, 4, LobbyPhase.Lobby, "1.0");
        using var beacon = new LanBeacon(port, 5_555, () => [lobby]);
        using var browser = new LanBrowser(port);
        var watch = Stopwatch.StartNew();
        long nextQuery = 0;
        while (browser.Lobbies().Count == 0 && watch.ElapsedMilliseconds < 5_000)
        {
            // A query at once and then every 500 ms. "elapsed % 500 < 2" missed its 2-ms window for whole seconds when
            // Thread.Sleep(1) slept a 15-ms timer tick on a loaded Windows runner, so no query went out at all (PR #74).
            if (watch.ElapsedMilliseconds >= nextQuery)
            {
                browser.Query();
                nextQuery = watch.ElapsedMilliseconds + 500;
            }

            beacon.Poll();
            browser.Poll();
            Thread.Sleep(1);
        }

        LanLobby found = Assert.Single(browser.Lobbies());
        Assert.Equal(5_555, found.Relay.Port);
        Assert.Equal("Living room", found.Lobby.Name);
    }

    [Fact]
    public void Answers_to_another_query_or_garbage_are_ignored()
    {
        byte[] answer = LanDiscovery.Answer(7, 1234, [new LobbySummary("A", "b", 1, 2, LobbyPhase.Lobby, "v")]);
        Assert.True(LanDiscovery.TryReadAnswer(answer, 7, out int port, out LobbySummary[] lobbies));
        Assert.Equal(1234, port);
        Assert.Single(lobbies);
        Assert.False(LanDiscovery.TryReadAnswer(answer, 8, out _, out _));
        Assert.False(LanDiscovery.TryReadAnswer(answer.AsSpan(0, 12), 7, out _, out _));
        Assert.False(LanDiscovery.TryReadQuery([1, 2, 3], out _));
        Assert.True(LanDiscovery.TryReadQuery(LanDiscovery.Query(9), out uint nonce));
        Assert.Equal(9u, nonce);
    }

    [Fact]
    public void Token_bucket_refills_at_its_rate()
    {
        var bucket = new TokenBucket(10, 20, 0);
        Assert.Equal(20, Enumerable.Range(0, 30).Count(_ => bucket.TryTake(1, 0)));
        Assert.False(bucket.TryTake(1, 50_000)); // half a token after 50 ms
        Assert.True(bucket.TryTake(1, 100_000));
        Assert.Equal(20, Enumerable.Range(0, 30).Count(_ => bucket.TryTake(1, 60_000_000)));
    }

    [Fact]
    public void Chat_window_admits_five_lines_per_ten_seconds()
    {
        var window = new SlidingWindowLimit(5, 10_000_000);
        Assert.Equal(5, Enumerable.Range(0, 8).Count(i => window.TryRecord(i * 1_000)));
        Assert.False(window.TryRecord(9_999_000));
        Assert.True(window.TryRecord(10_000_001));
        Assert.False(window.TryRecord(10_000_002));
    }
}

public sealed class KeyedRateLimitTests
{
    [Fact]
    public void The_least_recently_used_key_is_evicted_first_and_active_ones_keep_their_state()
    {
        var limit = new KeyedRateLimit<int>(capacity: 3, ratePerSecond: 1, burst: 2);
        Assert.True(limit.TryTake(1, 0));
        Assert.True(limit.TryTake(1, 0));
        Assert.False(limit.TryTake(1, 0)); // key 1 is out of tokens
        Assert.True(limit.TryTake(2, 0));
        Assert.True(limit.TryTake(3, 0));
        Assert.False(limit.TryTake(1, 0)); // touches 1: now 2 is the oldest
        Assert.True(limit.TryTake(4, 0)); // evicts 2, not 1
        Assert.Equal(3, limit.Count);
        Assert.False(limit.TryTake(1, 0)); // 1 kept its empty bucket
        Assert.True(limit.TryTake(2, 0)); // 2 starts fresh (evicts 3)
    }
}
