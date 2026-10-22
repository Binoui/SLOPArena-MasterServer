using MasterServer.Rooms;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MasterServer.Tests;

public sealed class VersusSlopballRoomTests
{
    private static RoomManager Room(int count)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Room:AdmittedCharacters:0"] = "Manki", ["Room:AdmittedArenas:0"] = "slop_pit",
            ["Room:AdmittedArcade:0"] = "slopball-versus:slopball",
        }).Build();
        var manager = new RoomManager(TimeProvider.System, config);
        for (int id = 1; id <= count; id++) manager.Connect(id, $"connection-{id}");
        var room = manager.CreateRoom(1, "Volley", "P1").Snapshot!;
        for (int id = 2; id <= count; id++) manager.JoinRoom(room.Id, id, $"P{id}");
        return manager;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void BalancedCampsAndBallOnlyReachTheFrozenMatchRoster(int count)
    {
        var rooms = Room(count);
        Assert.Null(rooms.SetBallOnly(1, true).Error);
        Assert.NotNull(rooms.SetBallOnly(2, false).Error);
        rooms.StartStageSelect(1);
        Assert.Null(rooms.ChoosePick(1, RoomFormats.SlopballVersus, "slopball").Error);
        rooms.StartCharacterSelect(1);
        for (int id = 1; id <= count; id++) rooms.SelectCharacter(id, "Manki");
        var setup = rooms.BeginMatch(1, out var error);
        Assert.Null(error); Assert.NotNull(setup);
        Assert.True(setup!.BallOnly);
        Assert.Equal(count / 2, setup.Players.Count(p => p.Camp == "teal"));
        Assert.Equal(count / 2, setup.Players.Count(p => p.Camp == "clay"));
        Assert.NotNull(rooms.SelectCamp(1, "clay").Error);
        Assert.NotNull(rooms.SetBallOnly(1, false).Error);
    }

    [Fact]
    public void CampChangesUnlockThePlayerAndUnbalancedCampsCannotLaunch()
    {
        var rooms = Room(2);
        rooms.StartStageSelect(1); rooms.ChoosePick(1, RoomFormats.SlopballVersus, "slopball");
        rooms.StartCharacterSelect(1);
        rooms.SelectCharacter(1, "Manki"); rooms.SelectCharacter(2, "Manki");
        var changed = rooms.SelectCamp(1, "clay");
        Assert.False(changed.Snapshot!.Members[0].LockedIn);
        rooms.SelectCharacter(1, "Manki");
        Assert.Null(rooms.BeginMatch(1, out var error));
        Assert.StartsWith("unbalanced_camps:", error);
        Assert.NotNull(rooms.SelectCamp(1, "unknown").Error);
        rooms.SelectCamp(2, "teal"); rooms.SelectCharacter(2, "Manki");
        Assert.NotNull(rooms.BeginMatch(1, out error)); Assert.Null(error);
    }

    [Fact]
    public void ThreePlayersCannotChooseVersus()
    {
        var rooms = Room(3); rooms.StartStageSelect(1);
        Assert.StartsWith("room_size:", rooms.ChoosePick(1, RoomFormats.SlopballVersus, "slopball").Error);
    }
}
