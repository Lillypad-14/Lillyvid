using Dalamud.Plugin.Services;
using VideoSyncPrototype.Phone.Core.Net;
using VideoSyncPrototype.Phone.Core.Playback;
using VideoSyncPrototype.Phone.Core.Radio;
using VideoSyncPrototype.Phone.Core.Songs;
using VideoSyncPrototype.Phone.Core.Spotify;
using YoutubeExplode;

namespace VideoSyncPrototype.Phone.Core;

// The slice of Aetherphone's PhoneServices that the Music app depends on: HTTP + a disk-backed
// media cache for cover art, the radio-browser directory, YouTube-backed song search, and the
// NAudio players behind PlaybackHub. Construction order mirrors Aetherphone's PhoneServices.
//
// The players run on their own threads, so nothing here needs a per-frame tick — but they do own
// unmanaged audio devices, hence the explicit disposal chain from PhoneScreen.
internal sealed class MusicServices : IDisposable
{
    private const long MediaCacheBytes = 64L * 1024 * 1024;
    private const long AudioCacheBytes = 256L * 1024 * 1024;

    public HttpService Http { get; }
    public MediaCache Media { get; }
    public RadioService Radio { get; }
    public RadioPlayer RadioPlayer { get; }
    public SongSearchService SongSearch { get; }
    public SongPlayer SongPlayer { get; }
    public SongHistory History { get; }
    public SpotifyController Spotify { get; }
    public PlaybackHub Playback { get; }

    public MusicServices(Configuration configuration, DirectoryInfo configDirectory, ITextureProvider textures)
    {
        var cacheRoot = new DirectoryInfo(Path.Combine(configDirectory.FullName, "cache"));
        cacheRoot.Create();

        Http = new HttpService();
        Media = new MediaCache(textures,
            new DiskCache(new DirectoryInfo(Path.Combine(cacheRoot.FullName, "media")), MediaCacheBytes));

        Radio = new RadioService(Http);
        RadioPlayer = new RadioPlayer();

        var youtube = new YoutubeClient();
        SongSearch = new SongSearchService(youtube);
        SongPlayer = new SongPlayer(youtube,
            new DiskCache(new DirectoryInfo(Path.Combine(cacheRoot.FullName, "audio")), AudioCacheBytes));

        History = new SongHistory(configuration);
        Spotify = new SpotifyController(configuration);
        Playback = new PlaybackHub(RadioPlayer, SongPlayer, Spotify);
    }

    public void Dispose()
    {
        Playback.StopLocal();
        Spotify.Dispose();
        SongPlayer.Dispose();
        RadioPlayer.Dispose();
        SongSearch.Dispose();
        Radio.Dispose();
        Media.Dispose();
        Http.Dispose();
    }
}
