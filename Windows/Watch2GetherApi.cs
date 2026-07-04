using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace VideoSyncPrototype.Windows;

/// <summary>
/// Thin client for the official Watch2Gether REST API.
///
/// This replaces the old approach of driving w2g.tv's web page with brittle
/// JavaScript (clicking "Create room", pasting into the search box, fighting the
/// invite modal). A single authenticated POST now creates the room server-side and
/// preloads the video, which fixes three problems at once:
///   * no create-landing page or "here's your URL, click close" invite modal ever
///     appears, so there are no stray tabs/popups to clean up;
///   * the shared video is already the room's current item, so it autoplays the
///     moment anyone opens the room;
///   * we get the real room URL back directly instead of scraping it out of the
///     address bar.
/// </summary>
public static class Watch2GetherApi
{
    private const string CreateEndpoint = "https://api.w2g.tv/rooms/create.json";
    // Watch2Gether's public API does not expose a bulk room-delete endpoint; account
    // room cleanup is done from https://w2g.tv/en/account/dashboard/.

    // Reused across calls; a plugin makes very few of these so one client is plenty.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>
    /// Creates a temporary Watch2Gether room with <paramref name="videoUrl"/> already
    /// loaded, and returns the room the user can open/share. Throws
    /// <see cref="Watch2GetherApiException"/> with a plain-language message on failure.
    /// </summary>
    public static async Task<Watch2GetherRoom> CreateRoomAsync(string apiKey, string videoUrl)
    {
        var request = new CreateRoomRequest(apiKey, videoUrl, "#000000", "100");
        var body = JsonSerializer.Serialize(request);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsync(CreateEndpoint, content).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new Watch2GetherApiException("Couldn't reach Watch2Gether. Check your internet connection and try again.");
        }

        using (response)
        {
            var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new Watch2GetherApiException(DescribeError((int)response.StatusCode));
            }

            string? streamkey;
            try
            {
                using var document = JsonDocument.Parse(responseText);
                streamkey = document.RootElement.TryGetProperty("streamkey", out var element)
                    ? element.GetString()
                    : null;
            }
            catch (JsonException)
            {
                throw new Watch2GetherApiException("Watch2Gether returned an unexpected response. Try again in a moment.");
            }

            if (string.IsNullOrWhiteSpace(streamkey))
            {
                throw new Watch2GetherApiException("Watch2Gether did not return a room key. Try again in a moment.");
            }

            return new Watch2GetherRoom($"https://w2g.tv/rooms/{streamkey}");
        }
    }

    private static string DescribeError(int statusCode)
    {
        return statusCode switch
        {
            400 => "Watch2Gether rejected that video link. Make sure it's a full http(s) URL.",
            401 or 403 => "Your Watch2Gether API key was rejected. Double-check it in Settings.",
            404 => "The Watch2Gether API endpoint could not be found. The plugin may need an update.",
            429 => "Watch2Gether is rate-limiting new rooms. Wait a few seconds and try again.",
            >= 500 => "Watch2Gether is having server trouble right now. Try again shortly.",
            _ => $"Watch2Gether couldn't create the room (error {statusCode}).",
        };
    }

    private readonly record struct CreateRoomRequest(
        [property: JsonPropertyName("w2g_api_key")] string ApiKey,
        [property: JsonPropertyName("share")] string Share,
        [property: JsonPropertyName("bg_color")] string BackgroundColor,
        [property: JsonPropertyName("bg_opacity")] string BackgroundOpacity);
}

/// <summary>A Watch2Gether API failure carrying a message safe to show the user.</summary>
public sealed class Watch2GetherApiException(string message) : Exception(message);
