using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace ExampleUi;

public sealed class ExampleUiExtension : CoveExtensionBase
{
    // Identity & metadata live in extension.json (the single source of truth); CoveExtensionBase surfaces them.
    // CoveExtensionBase already implements IUIExtension; override GetUIManifest() to contribute UI pages,
    // settings tabs, or settings panels.

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
    }
}
