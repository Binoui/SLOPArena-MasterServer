namespace MasterServer.Rooms;

/// <summary>
/// What a Room plays (ADR-0040 in the game repository). Wire names match the game's
/// <c>MatchFormats</c>; Master validates the Room size each format admits.
/// </summary>
public static class RoomFormats
{
    public const string Stock = "stock";
    public const string SlopballCoop = "slopball-coop";
    public const string SlopballVersus = "slopball-versus";

    public const string CourseRace = "course-race";
    public const string CourseCoop = "course-coop";
    public static bool IsCourse(string format) => format is CourseRace or CourseCoop;

    public static bool IsArcade(string format) => format is SlopballCoop or SlopballVersus or CourseRace or CourseCoop;

    /// <summary>Whether a Room of <paramref name="members"/> can play <paramref name="format"/>.</summary>
    public static bool Fits(string format, int members) => format switch
    {
        Stock or SlopballCoop or CourseRace or CourseCoop => members is >= 2 and <= 4,
        SlopballVersus => members is 2 or 4,
        _ => false,
    };
}
