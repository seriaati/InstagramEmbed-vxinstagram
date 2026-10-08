using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using InstagramEmbed.Application.Models;
using Microsoft.Extensions.Caching.Memory;

namespace InstagramEmbed.Application.Services;

/// <summary>
/// Fetches Instagram posts via the bundled snapsave Node service and caches
/// them in-process memory. 
/// </summary>
public sealed class PostCacheService
{
    private readonly IMemoryCache _cache;
    private readonly HttpClient _http;
    private readonly HttpClient _metaHttp;
    private readonly ILogger<PostCacheService> _logger;
    private readonly string _snapSaveBase;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(4);

    public PostCacheService(IMemoryCache cache, IHttpClientFactory factory,
        ILogger<PostCacheService> logger, IConfiguration config)
    {
        _cache = cache;
        _http = factory.CreateClient("snapsave");
        _metaHttp = factory.CreateClient("igmeta");
        _logger = logger;
        var port = config.GetValue<int>("SnapSave:Port", 3200);
        _snapSaveBase = $"http://localhost:{port}";
    }

    public async Task<CachedPost?> GetOrFetchAsync(string cacheId, string instagramUrl)
    {
        if (_cache.TryGetValue(cacheId, out CachedPost? cached))
            return cached;

        var pageTask = FetchPostPageAsync(instagramUrl);
        var post = await FetchFromSnapSaveAsync(cacheId, instagramUrl);
        if (post == null) return null;

        var page = await pageTask;
        if (page != null) ApplyOpenGraph(post, page);

        _cache.Set(cacheId, post, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheTtl,
            Size = 1
        });

        return post;
    }

    private async Task<CachedPost?> FetchFromSnapSaveAsync(string cacheId, string instagramUrl)
    {
        string? json = "";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await _http.GetAsync(
                $"{_snapSaveBase}/igdl?url={Uri.EscapeDataString(instagramUrl)}", cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("snapsave returned {Status} for {Url}", response.StatusCode, instagramUrl);
                return null;
            }

            json = await response.Content.ReadAsStringAsync(cts.Token);
            _logger.LogDebug("snapsave raw response: {Json}", json);
            _logger.LogInformation("snapsave returned {Status} for {Url}, {json}", response.StatusCode, instagramUrl, json);

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var snap = JsonSerializer.Deserialize<SnapSaveResponse>(json, opts);

            if (snap?.success != true || snap.data?.media == null || snap.data.media.Count == 0)
            {
                _logger.LogWarning("snapsave returned no media for {Url}. Response: {Json}", instagramUrl, json);
                return null;
            }

            return new CachedPost
            {
                ShortCode = cacheId,
                RawUrl = instagramUrl,
                Media = snap.data.media.Select(m => new CachedMedia
                {
                    Url = m.url,
                    MediaType = m.type,
                    ThumbnailUrl = m.thumbnail ?? m.url
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch {Url} from snapsave {json}", instagramUrl, json);
            return null;
        }
    }

    // Instagram serves Open Graph tags (author + full caption) to link-preview crawlers without a login.
    private static readonly Regex OgTagRegex = new(
        "<meta\\s+property=\"og:(title|url|description)\"\\s+content=\"([^\"]*)\"", RegexOptions.Compiled);

    // og:title = `{name} on Instagram: "{caption}"`
    private static readonly Regex OgTitleRegex = new(
        "^(.+?) on Instagram(?:: \"(.*)\")?$", RegexOptions.Compiled | RegexOptions.Singleline);

    // og:description = `{likes}, {comments} - {username} on {date}: "{caption}"`
    private static readonly Regex OgDescriptionRegex = new(
        " - ([A-Za-z0-9._]+) on [^:]+(?:: \"(.*)\")?$", RegexOptions.Compiled | RegexOptions.Singleline);

    private async Task<string?> FetchPostPageAsync(string instagramUrl)
    {
        try
        {
            return await _metaHttp.GetStringAsync(instagramUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch post page for {Url}", instagramUrl);
            return null;
        }
    }

    private static void ApplyOpenGraph(CachedPost post, string html)
    {
        var og = new Dictionary<string, string>();
        foreach (Match m in OgTagRegex.Matches(html))
            og.TryAdd(m.Groups[1].Value, WebUtility.HtmlDecode(m.Groups[2].Value));

        var title = og.TryGetValue("title", out var t) ? OgTitleRegex.Match(t) : Match.Empty;
        var description = og.TryGetValue("description", out var d) ? OgDescriptionRegex.Match(d) : Match.Empty;

        if (title.Success)
            post.AuthorName = title.Groups[1].Value;

        string? caption = title.Groups[2].Success ? title.Groups[2].Value
            : description.Groups[2].Success ? description.Groups[2].Value
            : null;
        if (!string.IsNullOrWhiteSpace(caption))
            post.Caption = caption;

        string? username = og.TryGetValue("url", out var url) ? UsernameFromPostUrl(url) : null;
        username ??= description.Success ? description.Groups[1].Value : null;
        if (username != null)
            post.AuthorUsername = username;
    }

    // og:url = https://www.instagram.com/{username}/p/{shortcode}/ (or /reel/, /tv/)
    private static string? UsernameFromPostUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        return segments.Length >= 3 && segments[1] is "p" or "reel" or "tv" ? segments[0] : null;
    }
}