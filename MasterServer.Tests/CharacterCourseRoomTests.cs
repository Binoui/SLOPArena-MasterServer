using MasterServer.Rooms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace MasterServer.Tests;

public sealed class CharacterCourseRoomTests
{
    [Theory]
    [InlineData(RoomFormats.CourseRace, false, "FightGuy", "Manki")]
    [InlineData(RoomFormats.CourseRace, false, "Manki", "Manki")]
    [InlineData(RoomFormats.CourseCoop, false, "FightGuy", "Manki")]
    [InlineData(RoomFormats.CourseCoop, false, "Manki", "Manki")]
    [InlineData(RoomFormats.CourseRace, true, "FightGuy", "Manki")]
    [InlineData(RoomFormats.CourseRace, true, "Manki", "Manki")]
    [InlineData(RoomFormats.CourseCoop, true, "FightGuy", "Manki")]
    [InlineData(RoomFormats.CourseCoop, true, "Manki", "Manki")]
    public void CourseLaunchesWithAnyAdmittedFighters(string format, bool legacy, string first, string second)
    {
        var entry = $"{format}:fightguy_pier" + (legacy ? ":RetiredFighter" : "");
        var rooms = Room(entry);
        Assert.Null(rooms.StartStageSelect(1).Error);
        Assert.Null(rooms.ChoosePick(1, format, "fightguy_pier").Error);
        Assert.Null(rooms.StartCharacterSelect(1).Error);
        var selection = rooms.GetMyRoom(1)!;
        Assert.Equal(new RoomArcadeEntry(format, "fightguy_pier"), Assert.Single(selection.AdmittedArcade!));
        Assert.All(selection.Members, member =>
        {
            Assert.Null(member.CharacterSelection);
            Assert.False(member.LockedIn);
        });
        Assert.Null(rooms.BeginMatch(1, out var pending));
        Assert.StartsWith("room_not_ready:", pending);
        Assert.Null(rooms.SelectCharacter(1, first).Error);
        Assert.Null(rooms.BeginMatch(1, out pending));
        Assert.StartsWith("room_not_ready:", pending);
        Assert.Null(rooms.SelectCharacter(2, second).Error);

        var launch = rooms.BeginMatch(1, out var error);

        Assert.Null(error);
        Assert.NotNull(launch);
        Assert.Equal(format, launch.Format);
        Assert.Equal("fightguy_pier", launch.ArenaName);
        Assert.Equal(new[] { first, second }, launch.Players.Select(player => player.Character));
        Assert.Equal(2, launch.Players.Select(player => player.Colour).Distinct().Count());
    }

    [Theory]
    [InlineData(RoomFormats.CourseRace, 2)]
    [InlineData(RoomFormats.CourseRace, 4)]
    [InlineData(RoomFormats.CourseCoop, 3)]
    public void CourseColoursStayDistinctAndSwappingRequiresBothMembersToLockInAgain(string format, int count)
    {
        var rooms = Room($"{format}:fightguy_pier", count);
        Assert.Null(rooms.StartStageSelect(1).Error);
        Assert.Null(rooms.ChoosePick(1, format, "fightguy_pier").Error);
        Assert.StartsWith("invalid_phase:", rooms.SelectColour(1, 1).Error);
        Assert.Null(rooms.StartCharacterSelect(1).Error);
        Assert.StartsWith("invalid_colour:", rooms.SelectColour(1, 4).Error);
        for (int id = 1; id <= count; id++)
            Assert.Null(rooms.SelectCharacter(id, id == 1 ? "FightGuy" : "Manki").Error);

        Assert.Null(rooms.SelectColour(1, 1).Error);
        var changed = rooms.GetMyRoom(1)!;
        Assert.False(changed.Members[0].LockedIn);
        Assert.False(changed.Members[1].LockedIn);
        Assert.Equal((byte)1, changed.Members[0].Colour);
        Assert.Equal((byte)0, changed.Members[1].Colour);
        Assert.Equal(count, changed.Members.Select(member => member.Colour).Distinct().Count());
        Assert.Null(rooms.BeginMatch(1, out var pending));
        Assert.StartsWith("room_not_ready:", pending);
        Assert.Null(rooms.SelectCharacter(1, "FightGuy").Error);
        Assert.Null(rooms.BeginMatch(1, out pending));
        Assert.StartsWith("room_not_ready:", pending);
        Assert.Null(rooms.SelectCharacter(2, "Manki").Error);

        var launch = rooms.BeginMatch(1, out var error);

        Assert.Null(error);
        Assert.NotNull(launch);
        Assert.Equal(count, launch.Players.Count);
        Assert.Equal(count, launch.Players.Select(player => player.Colour).Distinct().Count());
        Assert.Equal((byte)1, launch.Players[0].Colour);
        Assert.Equal((byte)0, launch.Players[1].Colour);
        Assert.StartsWith("invalid_phase:", rooms.SelectColour(1, 2).Error);
    }

    [Theory]
    [InlineData(RoomFormats.CourseRace)]
    [InlineData(RoomFormats.CourseCoop)]
    public void CourseRejectsNonAdmittedFighterWithoutChangingSelection(string format)
    {
        var rooms = Room($"{format}:fightguy_pier");
        Assert.Null(rooms.StartStageSelect(1).Error);
        Assert.Null(rooms.ChoosePick(1, format, "fightguy_pier").Error);
        Assert.Null(rooms.StartCharacterSelect(1).Error);
        Assert.StartsWith("character_not_admitted:", rooms.SelectCharacter(1, "Wibou").Error);
        Assert.Null(rooms.GetMyRoom(1)!.Members[0].CharacterSelection);
        Assert.False(rooms.GetMyRoom(1)!.Members[0].LockedIn);
        Assert.Null(rooms.SelectCharacter(1, "Manki").Error);
        Assert.StartsWith("character_not_admitted:", rooms.SelectCharacter(1, "Wibou").Error);
        Assert.Equal("Manki", rooms.GetMyRoom(1)!.Members[0].CharacterSelection);
    }

    [Theory]
    [InlineData(RoomFormats.CourseRace)]
    [InlineData(RoomFormats.CourseCoop)]
    public void LegacyCourseEntryLogsOneWarningNamingTheEntry(string format)
    {
        var entry = $"{format}:fightguy_pier:RetiredFighter";
        var logger = new Mock<ILogger<RoomManager>>();
        var rooms = Room(entry, logger: logger.Object);
        Assert.Null(rooms.StartStageSelect(1).Error);
        Assert.Null(rooms.ChoosePick(1, format, "fightguy_pier").Error);
        Assert.Null(rooms.StartCharacterSelect(1).Error);
        Assert.Null(rooms.SelectCharacter(1, "Manki").Error);
        Assert.Null(rooms.SelectCharacter(2, "FightGuy").Error);
        Assert.NotNull(rooms.BeginMatch(1, out _));

        logger.Verify(log => log.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(entry)),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        logger.VerifyNoOtherCalls();
    }

    [Fact]
    public void TwoPartCourseEntryDoesNotLogALegacyWarning()
    {
        var logger = new Mock<ILogger<RoomManager>>();
        Room("course-race:fightguy_pier", logger: logger.Object);
        logger.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("manki-mayhem:manki_mayhem:Manki")]
    [InlineData("slopball-coop:slopball:FightGuy")]
    [InlineData("slopball-versus:slopball:FightGuy")]
    [InlineData("course-race:fightguy_pier:FightGuy:extra")]
    [InlineData("course-coop::FightGuy")]
    [InlineData("course-race")]
    [InlineData("unknown:arena")]
    public void MalformedOrNonCourseThreePartEntriesStillRejectStartup(string entry)
    {
        Assert.Throws<InvalidOperationException>(() => Room(entry));
    }

    private static RoomManager Room(string entry, int count = 2, ILogger<RoomManager>? logger = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Room:AdmittedCharacters:0"] = "FightGuy",
            ["Room:AdmittedCharacters:1"] = "Manki",
            ["Room:AdmittedArenas:0"] = "slop_pit",
            ["Room:AdmittedArcade:0"] = entry,
        }).Build();
        var rooms = new RoomManager(TimeProvider.System, config, logger);
        for (int id = 1; id <= count; id++) rooms.Connect(id, "c" + id);
        var room = rooms.CreateRoom(1, "Courses", "P1").Snapshot!;
        for (int id = 2; id <= count; id++)
            Assert.Null(rooms.JoinRoom(room.Id, id, "P" + id).Error);
        return rooms;
    }
}
