using Xunit;

namespace Tzar2.Game.Tests;

public class GameInfoTests
{
    [Fact]
    public void Version_is_available()
    {
        Assert.Equal("Tzar2", GameInfo.Codename);
        Assert.False(string.IsNullOrWhiteSpace(GameInfo.Version));
    }
}
