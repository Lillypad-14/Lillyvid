using Dalamud.Configuration;

namespace VideoSyncPrototype;

/// <summary>
/// Persisted plugin settings. Stored by Dalamud next to the plugin and reloaded
/// on every launch, so the user only ever pastes their Watch2Gether API key once.
/// </summary>
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// Watch2Gether account API key. Used to create rooms server-side through the
    /// official API (no browser scraping). Empty until the user sets it up.
    /// </summary>
    public string Watch2GetherApiKey { get; set; } = string.Empty;

    /// <summary>Block YouTube ads in the in-world player.</summary>
    public bool AdBlockEnabled { get; set; } = true;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
