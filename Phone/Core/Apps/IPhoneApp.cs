using System.Numerics;

namespace VideoSyncPrototype.Phone.Core.Apps;

internal interface IPhoneApp : IDisposable
{
    string Id { get; }
    string DisplayName { get; }
    string Glyph { get; }
    Vector4 Accent => AppAccents.For(Id);
    int BadgeCount { get; }
    bool WantsTransparentScreen => false;
    void OnOpened();
    void OnClosed();
    void Draw(in PhoneContext context);
}
