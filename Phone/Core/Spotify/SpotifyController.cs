using SpotifyAPI.Web;

namespace VideoSyncPrototype.Phone.Core.Spotify;

// What the UI and PlaybackHub see. SpotifyState polls the Web API on a background task, so the
// render thread never touches its mutable fields directly — it reads an immutable snapshot that
// is swapped in atomically.
internal sealed record SpotifyTrack(
    string TrackId,
    string Title,
    string Artist,
    string Album,
    string ArtUrl,
    bool IsPlaying,
    bool Shuffle,
    string RepeatState,
    int ProgressMs,
    int DurationMs,
    int DeviceVolume,
    string DeviceName);

internal enum SpotifyStatus
{
    // No client id entered yet — the user must register their own Spotify app (Spotify policy).
    NeedsClientId,

    // Client id present, but we have no token: the user has not authorised us yet.
    NeedsLogin,

    // Authorising in the browser.
    Connecting,

    // Logged in, but the account is not Premium — the Web API refuses playback control.
    NotPremium,

    // Logged in and Premium, but no Spotify client is running / no active device to command.
    NoDevice,

    // Ready: a device is active and reporting a track.
    Ready,
}

// Wraps FantasyPlayer's SpotifyState with the bits the phone needs: persistence of the client id
// and refresh token, a thread-safe snapshot, and locally-interpolated progress (the API is only
// polled every few seconds, so the scrubber would otherwise jump).
internal sealed class SpotifyController : IDisposable
{
    private const string LoginUri = "http://127.0.0.1:2984/callback";
    private const int LoginPort = 2984;
    private const int RefreshMs = 3000;

    private readonly Configuration configuration;
    private readonly CancellationTokenSource lifetime = new();
    private SpotifyState? state;
    private volatile SpotifyTrack? track;
    private volatile bool loggedIn;
    private volatile bool premium;
    private volatile bool connecting;
    private long trackStampTicks;

    public SpotifyController(Configuration configuration)
    {
        this.configuration = configuration;
        if (HasClientId && configuration.SpotifyToken is not null)
        {
            Resume();
        }
    }

    public string ClientId => configuration.SpotifyClientId ?? string.Empty;
    public bool HasClientId => !string.IsNullOrWhiteSpace(ClientId);
    public bool IsPremium => premium;

    // True once Spotify is actually commandable — this is what makes PlaybackHub treat Spotify as
    // the active source in preference to the local radio/song players.
    public bool IsActive => loggedIn && premium && track is not null;

    public bool IsPlaying => track?.IsPlaying ?? false;

    public SpotifyStatus Status
    {
        get
        {
            if (!HasClientId)
            {
                return SpotifyStatus.NeedsClientId;
            }

            if (connecting)
            {
                return SpotifyStatus.Connecting;
            }

            if (!loggedIn)
            {
                return SpotifyStatus.NeedsLogin;
            }

            if (!premium)
            {
                return SpotifyStatus.NotPremium;
            }

            return track is null ? SpotifyStatus.NoDevice : SpotifyStatus.Ready;
        }
    }

    public SpotifyTrack? Track => track;

    // Spotify reports progress only on each poll, so advance it locally between polls while
    // playing. Keeps the Now Playing scrubber smooth instead of stepping every few seconds.
    public int ProgressMs
    {
        get
        {
            var current = track;
            if (current is null)
            {
                return 0;
            }

            if (!current.IsPlaying)
            {
                return current.ProgressMs;
            }

            var elapsed = Environment.TickCount64 - Interlocked.Read(ref trackStampTicks);
            return (int)Math.Clamp(current.ProgressMs + elapsed, 0, Math.Max(current.DurationMs, 1));
        }
    }

    public void SetClientId(string clientId)
    {
        configuration.SpotifyClientId = clientId.Trim();
        configuration.Save();
    }

    // Kicks off the OAuth PKCE flow: spins up the local callback server and opens the browser.
    public void Login()
    {
        if (!HasClientId || connecting)
        {
            return;
        }

        Reset();
        connecting = true;
        state = NewState();
        _ = state.StartAuth(lifetime.Token);
    }

    public void Logout()
    {
        Reset();
        configuration.SpotifyToken = null;
        configuration.Save();
    }

    private void Resume()
    {
        connecting = true;
        state = NewState();
        state.TokenResponse = configuration.SpotifyToken;
        _ = ResumeAsync();
    }

    private async Task ResumeAsync()
    {
        if (state is null)
        {
            return;
        }

        // The stored token is almost certainly expired; swap it for a fresh one before starting.
        await state.RequestToken().ConfigureAwait(false);
        await state.Start(lifetime.Token).ConfigureAwait(false);
    }

    private SpotifyState NewState()
    {
        var created = new SpotifyState(LoginUri, ClientId, LoginPort, RefreshMs);
        created.OnLoggedIn += OnLoggedIn;
        created.OnPlayerStateUpdate += OnPlayerStateUpdate;
        return created;
    }

    private void OnLoggedIn(PrivateUser user, PKCETokenResponse token)
    {
        connecting = false;
        loggedIn = true;
        premium = state?.IsPremiumUser ?? false;
        configuration.SpotifyToken = token;
        configuration.Save();
    }

    private void OnPlayerStateUpdate(CurrentlyPlayingContext context, FullTrack item)
    {
        if (context is null || item is null)
        {
            return;
        }

        var artist = item.Artists is { Count: > 0 } ? string.Join(", ", item.Artists.Select(a => a.Name)) : string.Empty;
        var art = item.Album?.Images is { Count: > 0 } images ? images[0].Url ?? string.Empty : string.Empty;
        Interlocked.Exchange(ref trackStampTicks, Environment.TickCount64);
        track = new SpotifyTrack(
            item.Id ?? string.Empty,
            item.Name ?? string.Empty,
            artist,
            item.Album?.Name ?? string.Empty,
            art,
            context.IsPlaying,
            context.ShuffleState,
            context.RepeatState ?? "off",
            context.ProgressMs,
            item.DurationMs,
            context.Device?.VolumePercent ?? 0,
            context.Device?.Name ?? string.Empty);
    }

    // ---- Controls. All no-op unless a device is actually commandable. ----------------------

    public void TogglePlayPause()
    {
        var current = track;
        if (state is null || current is null)
        {
            return;
        }

        state.PauseOrPlay(!current.IsPlaying);
        track = current with { IsPlaying = !current.IsPlaying };
        Interlocked.Exchange(ref trackStampTicks, Environment.TickCount64);
    }

    // Used when local playback (radio/YouTube) takes over, so the two never play over each other.
    public void Pause()
    {
        var current = track;
        if (state is null || current is null || !current.IsPlaying)
        {
            return;
        }

        state.PauseOrPlay(false);
        track = current with { IsPlaying = false };
    }

    public void Next() => state?.Skip(true);

    public void Previous() => state?.Skip(false);

    public void ToggleShuffle()
    {
        var current = track;
        if (state is null || current is null)
        {
            return;
        }

        _ = state.Shuffle(!current.Shuffle);
        track = current with { Shuffle = !current.Shuffle };
    }

    public void CycleRepeat()
    {
        var current = track;
        if (state is null || current is null)
        {
            return;
        }

        _ = state.SwapRepeatState();
        var next = current.RepeatState switch
        {
            "off" => "context",
            "context" => "track",
            _ => "off",
        };

        track = current with { RepeatState = next };
    }

    // Spotify volume is device volume, 0-100; the phone works in 0-1 like the local players.
    public void SetVolume(float normalized)
    {
        var current = track;
        if (state is null || current is null)
        {
            return;
        }

        var percent = (int)MathF.Round(Math.Clamp(normalized, 0f, 1f) * 100f);
        if (percent == current.DeviceVolume)
        {
            return;
        }

        state.SetVolume(percent);
        track = current with { DeviceVolume = percent };
    }

    private void Reset()
    {
        state?.Dispose();
        state = null;
        track = null;
        loggedIn = false;
        premium = false;
        connecting = false;
    }

    public void Dispose()
    {
        lifetime.Cancel();
        state?.Dispose();
        state = null;
        lifetime.Dispose();
    }
}
