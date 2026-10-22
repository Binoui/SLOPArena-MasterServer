using MasterServer.Lobbies;
using MasterServer.Rooms;
using Xunit;

namespace MasterServer.Tests;

public sealed class RoomClientProtocolTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("9", false)]
    [InlineData("11", false)]
    [InlineData("12", false)]
    [InlineData("+10", false)]
    [InlineData("ten", false)]
    public void VpsRefusesMissingDevelopmentAndOtherProtocols(string? value, bool expected)
        => Assert.Equal(expected, RoomClientProtocol.IsCompatible(value, isVps: true));

    [Fact]
    public void VpsAdmitsMastersProtocol()
        => Assert.True(RoomClientProtocol.IsCompatible(
            SteamMatchDescriptor.Protocol.ToString(System.Globalization.CultureInfo.InvariantCulture), isVps: true));

    [Theory]
    [InlineData(null, true)]
    [InlineData("0", true)]
    [InlineData("9", false)]
    [InlineData("ten", false)]
    public void DevelopmentAlsoAdmitsMissingAndDevelopmentProtocols(string? value, bool expected)
        => Assert.Equal(expected, RoomClientProtocol.IsCompatible(value, isVps: false));
}
