using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace ExampleScraper;

public sealed class ExampleScraperExtension : CoveExtensionBase, IScraperProvider
{
    // Identity & metadata live in extension.json (the single source of truth); CoveExtensionBase surfaces them.

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
    }

    public IReadOnlyList<ScraperDescriptor> GetScrapers() =>
    [
        new(
            "com.example.scraper.video",
            "Example video scraper",
            ScraperEntity.Video,
            ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
            ["example.com/videos/", "media.example.com/watch/"])
    ];

    public Task<ScrapedVideoDto?> ScrapeVideoAsync(ScraperRequest<VideoScrapeInput> request, CancellationToken ct)
    {
        var sourceUrl = request.Input.Url ?? request.Input.Urls.FirstOrDefault();
        if (!TryCreateSupportedUri(sourceUrl, out var uri))
            return Task.FromResult<ScrapedVideoDto?>(null);

        var title = string.IsNullOrWhiteSpace(request.Input.Title)
            ? "Example video"
            : request.Input.Title.Trim();

        return Task.FromResult<ScrapedVideoDto?>(new ScrapedVideoDto
        {
            Title = title,
            Code = request.Input.Code ?? Path.GetFileName(uri.AbsolutePath),
            Date = request.Input.Date,
            Details = request.Input.Details,
            Urls = [uri.ToString()],
            StudioName = "Example Studio",
            TagNames = ["example"],
        });
    }

    public Task<IReadOnlyList<ScrapedVideoDto>> SearchVideosAsync(ScraperRequest<string> request, CancellationToken ct)
    {
        var query = request.Input?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<ScrapedVideoDto>>([]);

        var slug = Uri.EscapeDataString(query.ToLowerInvariant().Replace(' ', '-'));
        IReadOnlyList<ScrapedVideoDto> results =
        [
            new ScrapedVideoDto
            {
                Title = query,
                Urls = [$"https://example.com/videos/{slug}"],
                StudioName = "Example Studio",
                TagNames = ["example"],
            },
        ];

        return Task.FromResult(results);
    }

    private static bool TryCreateSupportedUri(string? url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out uri!)
            && (string.Equals(uri.Host, "example.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "media.example.com", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        uri = null!;
        return false;
    }
}
