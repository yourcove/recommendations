using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace ExampleDownloader;

public sealed class ExampleDownloaderExtension : CoveExtensionBase, IDownloaderProvider
{
    // Identity & metadata live in extension.json (the single source of truth); CoveExtensionBase surfaces them.

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
    }

    public IReadOnlyList<DownloaderDescriptor> GetDownloaders() =>
    [
        new(
            "com.example.downloader.file",
            "Example downloader",
            DownloaderEntity.Video,
            ["https://example.com/*"],
            DownloaderCapabilities.InlineMetadata)
    ];

    public Task<DownloaderUrlMatch?> MatchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Task.FromResult<DownloaderUrlMatch?>(null);

        if (!string.Equals(uri.Host, "example.com", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<DownloaderUrlMatch?>(null);

        return Task.FromResult<DownloaderUrlMatch?>(new DownloaderUrlMatch(
            "com.example.downloader.file",
            uri.ToString(),
            Label: "Example media"));
    }
}