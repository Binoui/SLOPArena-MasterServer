using MasterServer.Rooms;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MasterServer.Tests;

public class MayhemRoomTests
{
    private static RoomManager Room(int count)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Room:AdmittedCharacters:0"] = "Manki", ["Room:AdmittedCharacters:1"] = "Wibou",
            ["Room:AdmittedArenas:0"] = "slop_pit", ["Room:AdmittedArcade:0"] = "manki-mayhem:manki_mayhem",
        }).Build();
        var rooms = new RoomManager(TimeProvider.System, config);
        for (int id = 1; id <= count; id++) rooms.Connect(id, $"connection-{id}");
        var room = rooms.CreateRoom(1, "Mayhem", "P1").Snapshot!;
        for (int id = 2; id <= count; id++) rooms.JoinRoom(room.Id, id, $"P{id}");
        return rooms;
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void MayhemFitsTwoToFourHumans(int members, bool fits)
    {
        Assert.True(RoomFormats.IsArcade(RoomFormats.MankiMayhem));
        Assert.Equal(fits, RoomFormats.Fits(RoomFormats.MankiMayhem, members));
    }

    [Theory]
    [InlineData(2, "off")]
    [InlineData(3, "shoves")]
    [InlineData(4, "on")]
    public void PickAllowsMirrorsAndFreeFightersAndFreezesTheRule(int count, string mode)
    {
        var rooms = Room(count);
        rooms.StartStageSelect(1);
        Assert.NotNull(rooms.ChoosePick(2, RoomFormats.MankiMayhem, "manki_mayhem", mode).Error);
        Assert.NotNull(rooms.ChoosePick(1, RoomFormats.MankiMayhem, "slop_pit", mode).Error);
        Assert.NotNull(rooms.ChoosePick(1, RoomFormats.MankiMayhem, "manki_mayhem", "invalid").Error);
        Assert.Null(rooms.ChoosePick(1, RoomFormats.MankiMayhem, "manki_mayhem", mode).Error);
        rooms.StartCharacterSelect(1);
        for (int id = 1; id <= count; id++) Assert.Null(rooms.SelectCharacter(id, id == count ? "Wibou" : "Manki").Error);
        var start = rooms.BeginMatch(1, out var error);
        Assert.Null(error); Assert.NotNull(start);
        Assert.Equal(count, start!.Players.Count);
        Assert.Equal(mode, start.FriendlyFire);
        Assert.NotNull(rooms.ChoosePick(1, RoomFormats.MankiMayhem, "manki_mayhem", "off").Error);
    }
}
