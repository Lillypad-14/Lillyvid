using VideoSyncPrototype.Phone.Core.Theme;

namespace VideoSyncPrototype.Phone.Core.Apps;

internal readonly struct PhoneContext
{
    public readonly Rect Content;
    public readonly PhoneTheme Theme;
    public readonly INavigator Navigation;

    public PhoneContext(Rect content, PhoneTheme theme, INavigator navigation)
    {
        Content = content;
        Theme = theme;
        Navigation = navigation;
    }
}
