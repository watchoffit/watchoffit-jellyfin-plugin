using System.Reflection;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Watchoffit.Configuration;

/// <summary>
/// Serves the plugin-owned configuration page translations.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/Watchoffit/Localization")]
public sealed class LocalizationController : ControllerBase
{
    private const string DefaultLocale = "en";
    private const string ResourcePrefix = "Jellyfin.Plugin.Watchoffit.Locale.";

    /// <summary>
    /// Returns the best matching catalog for the requested UI locale.
    /// </summary>
    /// <param name="locale">A BCP-47 locale such as <c>ru-RU</c> or <c>en-US</c>.</param>
    /// <returns>A translation dictionary. Unknown locales fall back to English.</returns>
    [HttpGet]
    public IActionResult Get([FromQuery] string? locale)
    {
        var normalizedLocale = NormalizeLocale(locale);
        var catalog = LoadCatalog(normalizedLocale) ?? LoadCatalog(DefaultLocale);
        return catalog is null ? NotFound() : Ok(catalog);
    }

    internal static Dictionary<string, string>? LoadCatalog(string locale)
    {
        var resourceName = ResourcePrefix + locale + ".json";
        using var stream = typeof(LocalizationController).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
    }

    internal static string NormalizeLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return DefaultLocale;
        }

        var language = locale.Trim().Replace('_', '-').Split('-', 2)[0].ToLowerInvariant();
        return language.Length == 2 && language.All(char.IsAsciiLetter) ? language : DefaultLocale;
    }
}
