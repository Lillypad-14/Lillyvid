using System.Numerics;
using VideoSyncPrototype.Phone.Core.Apps;

namespace VideoSyncPrototype.Phone.Apps.Games.Framework;

internal interface IMiniGame : IDisposable
{
    string Id { get; }
    string Title { get; }
    string Genre { get; }
    Vector4 Accent => AppAccents.For(Id);
    void Open();
    void Close();
    void Draw(in GameContext context);
}
