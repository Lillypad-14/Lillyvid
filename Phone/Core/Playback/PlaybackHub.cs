using VideoSyncPrototype.Phone.Core.Analytics;
using VideoSyncPrototype.Phone.Core.Localization;
using VideoSyncPrototype.Phone.Core.Radio;
using VideoSyncPrototype.Phone.Core.Songs;
using VideoSyncPrototype.Phone.Core.Spotify;

namespace VideoSyncPrototype.Phone.Core.Playback;

internal sealed class PlaybackHub
{
    private const long MinListenTicks = 3000;
    private readonly RadioPlayer radio;
    private readonly SongPlayer songs;
    private readonly SpotifyController? spotify;
    private float volume = 0.6f;
    private string listenStation = string.Empty;
    private long listenStartTicks;

    public PlaybackHub(RadioPlayer radio, SongPlayer songs, SpotifyController? spotify = null)
    {
        this.radio = radio;
        this.songs = songs;
        this.spotify = spotify;
        radio.Volume = volume;
        songs.Volume = volume;
    }

    public RadioPlayer Radio => radio;
    public SongPlayer Songs => songs;
    public SpotifyController? Spotify => spotify;

    // Spotify outranks the local players: it is a remote control over a client that is already
    // making sound, so if it is live it is what the user is actually hearing. The local players
    // are paused whenever Spotify takes over (and vice versa), so at most one source is audible.
    public bool SpotifyActive => spotify?.IsActive ?? false;
    public bool SongActive => !SpotifyActive && songs.State != SongPlaybackState.Stopped;
    public bool RadioActive => !SpotifyActive && radio.State != RadioPlaybackState.Stopped;
    public bool IsActive => SpotifyActive || songs.State != SongPlaybackState.Stopped ||
                            radio.State != RadioPlaybackState.Stopped;

    public bool IsPlaying =>
        SpotifyActive
            ? spotify!.IsPlaying
            : SongActive
                ? songs.State == SongPlaybackState.Playing && !songs.IsPaused
                : radio.State == RadioPlaybackState.Playing;

    public bool IsPaused =>
        SpotifyActive ? !spotify!.IsPlaying :
        SongActive ? songs.IsPaused : radio.State == RadioPlaybackState.Paused;

    public string Title => SpotifyActive
        ? spotify!.Track?.Title ?? string.Empty
        : SongActive
            ? songs.CurrentTitle
            : radio.CurrentStation;

    public string Subtitle => SpotifyActive
        ? spotify!.Track?.Artist ?? string.Empty
        : SongActive
            ? SongSubtitle()
            : RadioStateLabel(radio.State);

    public bool HasQueue => SpotifyActive || (SongActive ? songs.HasQueue : radio.HasQueue);

    public float Volume
    {
        get => SpotifyActive ? (spotify!.Track?.DeviceVolume ?? 0) / 100f : volume;
        set
        {
            if (SpotifyActive)
            {
                // Spotify's volume is the remote device's, not ours — don't touch the local mixers.
                spotify!.SetVolume(value);
                return;
            }

            volume = Math.Clamp(value, 0f, 1f);
            radio.Volume = volume;
            songs.Volume = volume;
        }
    }

    public void PlayStations(RadioStation[] stations, int index)
    {
        FlushRadioListen();
        spotify?.Pause();
        songs.Stop();
        radio.Play(stations, index);
        BeginRadioListen();
    }

    public void PlaySongs(Song[] list, int index)
    {
        FlushRadioListen();
        spotify?.Pause();
        radio.Stop();
        songs.Play(list, index);
    }

    // Called when the user starts Spotify from the app, so local audio doesn't play underneath it.
    public void StopLocal()
    {
        FlushRadioListen();
        radio.Stop();
        songs.Stop();
    }

    public void Next()
    {
        if (SpotifyActive)
        {
            spotify!.Next();
            return;
        }

        if (SongActive)
        {
            songs.Next();
            return;
        }

        FlushRadioListen();
        radio.Next();
        BeginRadioListen();
    }

    public void Previous()
    {
        if (SpotifyActive)
        {
            spotify!.Previous();
            return;
        }

        if (SongActive)
        {
            songs.Previous();
            return;
        }

        FlushRadioListen();
        radio.Previous();
        BeginRadioListen();
    }

    public void Stop()
    {
        FlushRadioListen();
        spotify?.Pause();
        radio.Stop();
        songs.Stop();
    }

    public void TogglePlayPause()
    {
        if (SpotifyActive)
        {
            spotify!.TogglePlayPause();
            return;
        }

        if (SongActive)
        {
            if (songs.IsPaused)
            {
                songs.Resume();
            }
            else
            {
                songs.Pause();
            }

            return;
        }

        if (radio.State == RadioPlaybackState.Paused)
        {
            radio.Resume();
            BeginRadioListen();
            return;
        }

        if (RadioActive)
        {
            FlushRadioListen();
            radio.Pause();
        }
    }

    private void BeginRadioListen()
    {
        listenStation = radio.CurrentStation;
        listenStartTicks = Environment.TickCount64;
    }

    private void FlushRadioListen()
    {
        if (listenStartTicks == 0)
        {
            return;
        }

        var elapsedTicks = Environment.TickCount64 - listenStartTicks;
        listenStartTicks = 0;
        if (elapsedTicks >= MinListenTicks && listenStation.Length > 0)
        {
            Plugin.Analytics.Track(AnalyticsEvents.MusicListen(listenStation, elapsedTicks / 1000d));
        }
    }

    private string SongSubtitle()
    {
        return songs.State switch
        {
            SongPlaybackState.Resolving => Loc.T(L.Common.Loading),
            SongPlaybackState.Buffering => Loc.T(L.Music.Buffering),
            SongPlaybackState.Failed => Loc.T(L.Music.PlaybackFailed),
            _ => songs.CurrentAuthor,
        };
    }

    private static string RadioStateLabel(RadioPlaybackState state)
    {
        return state switch
        {
            RadioPlaybackState.Buffering => Loc.T(L.Music.Buffering),
            RadioPlaybackState.Playing => Loc.T(L.Music.NowPlayingState),
            RadioPlaybackState.Paused => Loc.T(L.Music.Paused),
            RadioPlaybackState.Failed => Loc.T(L.Music.ConnectionLost),
            _ => string.Empty,
        };
    }
}
