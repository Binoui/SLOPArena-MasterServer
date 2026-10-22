using MasterServer.Rooms;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MasterServer.Tests;

public sealed class CharacterCourseRoomTests
{
    [Theory]
    [InlineData(RoomFormats.CourseRace, 2)]
    [InlineData(RoomFormats.CourseRace, 4)]
    [InlineData(RoomFormats.CourseCoop, 3)]
    public void CourseSetsFighterColoursSwapAndLockedRosterLaunches(string format, int count)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Room:AdmittedCharacters:0"] = "FightGuy", ["Room:AdmittedCharacters:1"] = "Manki",
            ["Room:AdmittedArenas:0"] = "slop_pit",
            ["Room:AdmittedArcade:0"] = $"{format}:fightguy_pier:FightGuy",
        }).Build();
        var rooms = new RoomManager(TimeProvider.System, config);
        for (int id = 1; id <= count; id++) rooms.Connect(id, "c" + id);
        var room = rooms.CreateRoom(1, "Courses", "P1").Snapshot!;
        for (int id = 2; id <= count; id++) rooms.JoinRoom(room.Id, id, "P" + id);
        rooms.StartStageSelect(1);
        Assert.Null(rooms.ChoosePick(1, format, "fightguy_pier").Error);
        Assert.NotNull(rooms.SelectColour(1, 1).Error); // only Character Select
        rooms.StartCharacterSelect(1);
        Assert.Equal("FightGuy", rooms.GetMyRoom(1)!.AdmittedArcade!.Single().Fighter);
        Assert.All(rooms.GetMyRoom(1)!.Members, m => Assert.Equal("FightGuy", m.CharacterSelection));
        Assert.NotNull(rooms.SelectCharacter(1, "Manki").Error);
        for (int id = 1; id <= count; id++) rooms.SelectCharacter(id, "FightGuy");
        Assert.Null(rooms.SelectColour(1, 1).Error);
        var changed = rooms.GetMyRoom(1)!;
        Assert.False(changed.Members[0].LockedIn); Assert.False(changed.Members[1].LockedIn);
        Assert.Equal((byte)1, changed.Members[0].Colour); Assert.Equal((byte)0, changed.Members[1].Colour);
        Assert.Null(rooms.BeginMatch(1, out _));
        rooms.SelectCharacter(1, "FightGuy"); rooms.SelectCharacter(2, "FightGuy");
        var launch = rooms.BeginMatch(1, out var error);
        Assert.Null(error); Assert.NotNull(launch); Assert.Equal(format, launch!.Format);
        Assert.Equal(count, launch.Players.Select(p => p.Colour).Distinct().Count());
        Assert.All(launch.Players, p => Assert.Equal("FightGuy", p.Character));
        Assert.NotNull(rooms.SelectColour(1, 2).Error); // frozen Match
    }
}
