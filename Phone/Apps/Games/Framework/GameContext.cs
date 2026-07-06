using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Games;
using VideoSyncPrototype.Phone.Core.Theme;

namespace VideoSyncPrototype.Phone.Apps.Games.Framework;

internal readonly struct GameContext
{
    public readonly Rect Body;
    public readonly PhoneTheme Theme;
    public readonly GameStatsStore Stats;
    public readonly float DeltaSeconds;

    public GameContext(Rect body, PhoneTheme theme, GameStatsStore stats, float deltaSeconds)
    {
        Body = body;
        Theme = theme;
        Stats = stats;
        DeltaSeconds = deltaSeconds;
    }
}
