// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.Shared.Configuration.Configuration.Identity;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration.Interfaces;
using TenantInfrastructure.Abstractions;

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.TestInfrastructure;

/// <summary>
/// Reusable in-process Razor render harness for the STS host project.
/// <para>
/// Builds a minimal <c>WebHostBuilder</c> backed by <see cref="TestServer"/> that loads the
/// STS host's compiled views via <c>ApplicationPartManager</c>, exposes a stub
/// <see cref="IStringLocalizerFactory"/> with caller-supplied resource values (so partials
/// that depend on <c>IViewLocalizer</c> render predictably), and surfaces a mutable
/// <see cref="FakeRootConfiguration"/> + <see cref="FakeTenantContextAccessor"/> so each
/// property-test case can reconfigure <c>AdminConfiguration</c> URLs and tenant context
/// without rebuilding the host.
/// </para>
/// <para>
/// The harness is intentionally narrow in scope: it spins up just enough services to call
/// <see cref="IRazorViewEngine.FindView"/>, then writes <see cref="IView.RenderAsync"/>
/// output to a <see cref="StringWriter"/>. It does NOT bring up Identity, IdentityServer,
/// EF Core, or the Phone-OTP infrastructure — keeping per-test cost in milliseconds.
/// </para>
/// </summary>
public sealed class RazorRenderHost : IDisposable
{
    private readonly IHost _host;

    public FakeRootConfiguration RootConfiguration { get; }
    public FakeTenantContextAccessor TenantContextAccessor { get; }
    public FakeStringLocalizerStore LocalizerStore { get; }

    /// <summary>
    /// Mutable accessor backing <see cref="IOptions{RequestLocalizationOptions}"/> so tests
    /// can reconfigure <c>SupportedUICultures</c> between renders without rebuilding the host.
    /// Used by <c>_LoginLanguageSwitcher</c>, which reads <c>LocOptions.Value.SupportedUICultures</c>.
    /// </summary>
    public MutableRequestLocalizationOptions LocalizationOptions { get; }

    public IServiceProvider Services => _host.Services;

    public RazorRenderHost(Action<IServiceCollection>? configureServices = null)
    {
        RootConfiguration = new FakeRootConfiguration();
        TenantContextAccessor = new FakeTenantContextAccessor();
        LocalizerStore = new FakeStringLocalizerStore();
        LocalizationOptions = new MutableRequestLocalizationOptions();

        // Seed default resource values for every key referenced by the partials we render.
        // Tests can mutate LocalizerStore.Resources directly to override per-case.
        SeedDefaultResources(LocalizerStore.Resources);

        var stsHostAssembly = typeof(Skoruba.Duende.IdentityServer.STS.Identity.Program).Assembly;
        var applicationName = stsHostAssembly.GetName().Name!;

        _host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                // Pin the application name to the STS host assembly so view-localization
                // resource lookups (and resx assembly resolution) target it.
                web.UseSetting(WebHostDefaults.ApplicationKey, applicationName);

                web.ConfigureServices(services =>
                {
                    services.AddLogging();

                    // Replace the default IStringLocalizerFactory before AddViewLocalization
                    // wires up its IHtmlLocalizerFactory chain, so IViewLocalizer ultimately
                    // delegates to our stub instead of trying to load resx files.
                    services.RemoveAll<IStringLocalizerFactory>();
                    services.AddSingleton<IStringLocalizerFactory>(_ => new FakeStringLocalizerFactory(LocalizerStore));

                    services.AddLocalization(opts => opts.ResourcesPath = "Resources");

                    var mvc = services
                        .AddControllersWithViews()
                        .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix,
                            opts => opts.ResourcesPath = "Resources")
                        .AddDataAnnotationsLocalization();

                    // Reapply the stub registration after AddLocalization (which may
                    // re-register the resource-manager-based factory via TryAdd).
                    services.RemoveAll<IStringLocalizerFactory>();
                    services.AddSingleton<IStringLocalizerFactory>(_ => new FakeStringLocalizerFactory(LocalizerStore));

                    // Make the STS host assembly's compiled views discoverable by
                    // IRazorViewEngine.FindView through the ApplicationPartManager.
                    mvc.AddApplicationPart(stsHostAssembly);

                    // Mutable IRootConfiguration / ITenantContextAccessor surface that tests
                    // reconfigure between RenderPartialAsync calls.
                    services.AddSingleton<IRootConfiguration>(RootConfiguration);
                    services.AddSingleton<ITenantContextAccessor>(TenantContextAccessor);
                    services.AddHttpContextAccessor();

                    // Replace the framework-registered IOptions<RequestLocalizationOptions>
                    // (added by AddViewLocalization above) with our mutable accessor so tests
                    // can dictate SupportedUICultures per case. RemoveAll is necessary because
                    // AddLocalization/AddViewLocalization register the options chain via
                    // TryAdd, which would otherwise win over our singleton.
                    services.RemoveAll<IOptions<RequestLocalizationOptions>>();
                    services.AddSingleton<IOptions<RequestLocalizationOptions>>(LocalizationOptions);

                    configureServices?.Invoke(services);
                });

                web.Configure(app =>
                {
                    // Endpoints aren't actually invoked — we only need the DI graph and the
                    // view engine. Configure() is required for the WebHost to start cleanly.
                    app.UseRouting();
                    app.UseEndpoints(_ => { });
                });
            })
            .Start();
    }

    /// <summary>
    /// Renders <paramref name="partialName"/> to an HTML string using the configured
    /// <see cref="IRazorViewEngine"/>. Search order matches MVC's defaults:
    /// <c>/Views/{Controller}/{partialName}.cshtml</c> then
    /// <c>/Views/Shared/{partialName}.cshtml</c>.
    /// </summary>
    /// <param name="partialName">Partial name relative to the conventional view roots
    /// (e.g. <c>"Common/_LoginFooter"</c>).</param>
    /// <param name="model">Optional view model.</param>
    /// <param name="viewData">Optional ViewData entries to merge in.</param>
    /// <param name="requestHost">Optional <see cref="HostString"/> to apply to the
    /// synthetic <c>HttpContext.Request.Host</c>. Used by partials such as
    /// <c>_LoginTenantPill</c> that read <c>Context.Request.Host.Value</c>.</param>
    /// <param name="requestPath">Optional path to apply to the synthetic
    /// <c>HttpContext.Request.Path</c>. Used by <c>_LoginLanguageSwitcher</c>'s
    /// <c>returnUrl</c> hidden-input fallback when the model omits it.</param>
    /// <param name="requestQuery">Optional query string (including the leading <c>?</c>)
    /// to apply to the synthetic <c>HttpContext.Request.QueryString</c>.</param>
    /// <param name="requestCulture">Optional <see cref="RequestCulture"/> to surface via
    /// <see cref="IRequestCultureFeature"/>. Used by <c>_LoginLanguageSwitcher</c> to
    /// pre-select the option matching the resolved request UI culture.</param>
    public async Task<string> RenderPartialAsync(
        string partialName,
        object? model = null,
        IDictionary<string, object?>? viewData = null,
        HostString? requestHost = null,
        string? requestPath = null,
        string? requestQuery = null,
        RequestCulture? requestCulture = null)
    {
        using var scope = Services.CreateScope();
        var sp = scope.ServiceProvider;
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        if (requestHost.HasValue)
        {
            httpContext.Request.Host = requestHost.Value;
        }
        if (requestPath is not null)
        {
            httpContext.Request.Path = new PathString(requestPath);
        }
        if (requestQuery is not null)
        {
            httpContext.Request.QueryString = new QueryString(requestQuery);
        }
        if (requestCulture is not null)
        {
            // Surface the request culture via IRequestCultureFeature so partials such as
            // _LoginLanguageSwitcher can mark the matching <option> as selected.
            httpContext.Features.Set<IRequestCultureFeature>(
                new RequestCultureFeature(requestCulture, provider: null));
        }
        var routeData = new RouteData();
        // The view engine only finds Shared partials reliably when a controller token
        // exists in route values. Use a synthetic name; the partial's filesystem path
        // is what actually matters.
        routeData.Values["controller"] = "Account";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewResult = viewEngine.FindView(actionContext, partialName, isMainPage: false);
        if (!viewResult.Success)
        {
            // Fall back to absolute-path lookup so callers can pass either the partial
            // name or the full application-relative path.
            var absolutePath = partialName.StartsWith("~", StringComparison.Ordinal)
                ? partialName
                : $"~/Views/Shared/{partialName}.cshtml";
            viewResult = viewEngine.GetView(executingFilePath: null, viewPath: absolutePath, isMainPage: false);
        }

        if (!viewResult.Success)
        {
            var locations = string.Join("\n  ", viewResult.SearchedLocations ?? Array.Empty<string>());
            throw new InvalidOperationException(
                $"RazorRenderHost could not find partial '{partialName}'. Searched locations:\n  {locations}");
        }

        var view = viewResult.View;

        var viewDictionary = new ViewDataDictionary<object?>(
            new EmptyModelMetadataProvider(),
            new ModelStateDictionary())
        {
            Model = model
        };
        if (viewData is not null)
        {
            foreach (var kvp in viewData)
            {
                viewDictionary[kvp.Key] = kvp.Value;
            }
        }

        var tempData = new TempDataDictionary(httpContext, tempDataProvider);

        await using var writer = new StringWriter();
        var viewContext = new ViewContext(
            actionContext,
            view,
            viewDictionary,
            tempData,
            writer,
            new HtmlHelperOptions());

        await view.RenderAsync(viewContext);
        return writer.ToString();
    }

    public void Dispose() => _host.Dispose();

    /// <summary>
    /// Default resource pool used by every partial in this harness. Property 7 only needs
    /// the 4 footer keys, but we seed the full chrome surface so other Razor-render
    /// property tests (Properties 8/12/13/14) can reuse this harness without re-seeding.
    /// </summary>
    private static void SeedDefaultResources(IDictionary<string, string> resources)
    {
        // Footer keys (Property 7).
        resources["Login.TermsNotice"] = "By continuing you agree to our {0} and {1}.";
        resources["Login.TermsLink"] = "Terms of Service";
        resources["Login.PrivacyLink"] = "Privacy Policy";
        resources["Login.SupportLink"] = "Support";

        // Tenant pill (Property 8).
        resources["Login.TenantPillLabel"] = "Current tenant";
        resources["Login.TenantPillAriaLabel"] = "Current tenant";

        // Header (Properties not yet implemented but harness is shared).
        resources["Login.Nav.Products"] = "Products";
        resources["Login.Nav.Features"] = "Features";
        resources["Login.Nav.Pricing"] = "Pricing";
        resources["Login.HeaderCtaLogin"] = "Sign in";

        // Language switcher (Properties 12-14).
        resources["Layout.Language"] = "Language";
    }
}

/// <summary>
/// Mutable accessor wrapping a single <see cref="RequestLocalizationOptions"/> instance.
/// <para>
/// Registered as the singleton <see cref="IOptions{TOptions}"/> for
/// <see cref="RequestLocalizationOptions"/> in <see cref="RazorRenderHost"/> so tests can
/// reconfigure <c>SupportedUICultures</c> between renders by writing to
/// <c>harness.LocalizationOptions.Value.SupportedUICultures</c> or by calling
/// <see cref="SetSupportedUICultures"/>.
/// </para>
/// <para>
/// Why this class exists: <see cref="ServiceCollectionDescriptorExtensions"/> options binding
/// (<c>services.Configure&lt;RequestLocalizationOptions&gt;</c>) constructs a fresh options
/// snapshot per resolution, which means there is no observable instance that a test can
/// mutate after host start. By replacing the framework registration with this single shared
/// instance, the partial's <c>@inject IOptions&lt;RequestLocalizationOptions&gt;</c> always
/// observes the most recent test mutation.
/// </para>
/// </summary>
public sealed class MutableRequestLocalizationOptions : IOptions<RequestLocalizationOptions>
{
    public RequestLocalizationOptions Value { get; }

    public MutableRequestLocalizationOptions()
    {
        Value = new RequestLocalizationOptions();
        Value.SupportedUICultures = new List<CultureInfo>();
        Value.SupportedCultures = new List<CultureInfo>();
    }

    /// <summary>
    /// Replaces <see cref="RequestLocalizationOptions.SupportedUICultures"/> with the supplied
    /// list. The list is wrapped in a fresh <see cref="List{T}"/> so the harness owns the
    /// reference and tests cannot accidentally mutate it through external aliases.
    /// </summary>
    public void SetSupportedUICultures(IList<CultureInfo> cultures)
    {
        if (cultures is null) throw new ArgumentNullException(nameof(cultures));
        Value.SupportedUICultures = new List<CultureInfo>(cultures);
    }
}


public sealed class FakeRootConfiguration : IRootConfiguration
{
    private AdminConfiguration _adminConfiguration = new();
    private RegisterConfiguration _registerConfiguration = new();

    public AdminConfiguration AdminConfiguration => _adminConfiguration;

    public RegisterConfiguration RegisterConfiguration => _registerConfiguration;

    public void SetAdminConfiguration(AdminConfiguration adminConfiguration)
        => _adminConfiguration = adminConfiguration ?? throw new ArgumentNullException(nameof(adminConfiguration));

    public void SetRegisterConfiguration(RegisterConfiguration registerConfiguration)
        => _registerConfiguration = registerConfiguration ?? throw new ArgumentNullException(nameof(registerConfiguration));
}

/// <summary>
/// Mutable <see cref="ITenantContextAccessor"/>. Defaults to <see cref="Current"/> = null
/// (no tenant), which causes <c>_LoginTenantPill</c> to short-circuit. Tests can call
/// <see cref="Set"/> to simulate an active tenant context.
/// </summary>
public sealed class FakeTenantContextAccessor : ITenantContextAccessor
{
    public TenantContext? Current { get; private set; }

    public void Set(TenantContext context) => Current = context;

    public void Clear() => Current = null;
}

/// <summary>
/// Backing store for <see cref="FakeStringLocalizerFactory"/>. Tests mutate
/// <see cref="Resources"/> directly to override resource lookups without recreating the host.
/// </summary>
public sealed class FakeStringLocalizerStore
{
    public IDictionary<string, string> Resources { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Stub <see cref="IStringLocalizerFactory"/> backed by an in-memory dictionary. Replaces
/// the default <c>ResourceManagerStringLocalizerFactory</c> so the harness does not depend
/// on resx files being embedded for the partial under test.
/// </summary>
public sealed class FakeStringLocalizerFactory : IStringLocalizerFactory
{
    private readonly FakeStringLocalizerStore _store;

    public FakeStringLocalizerFactory(FakeStringLocalizerStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public IStringLocalizer Create(Type resourceSource) => new FakeStringLocalizer(_store);

    public IStringLocalizer Create(string baseName, string location) => new FakeStringLocalizer(_store);
}

/// <summary>
/// Stub <see cref="IStringLocalizer"/> that returns the configured resource value when
/// found, or the key with <c>resourceNotFound: true</c> when missing.
/// </summary>
public sealed class FakeStringLocalizer : IStringLocalizer
{
    private readonly FakeStringLocalizerStore _store;

    public FakeStringLocalizer(FakeStringLocalizerStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public LocalizedString this[string name]
    {
        get
        {
            if (_store.Resources.TryGetValue(name, out var value))
            {
                return new LocalizedString(name, value, resourceNotFound: false);
            }
            return new LocalizedString(name, name, resourceNotFound: true);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            if (_store.Resources.TryGetValue(name, out var value))
            {
                return new LocalizedString(name, string.Format(value, arguments), resourceNotFound: false);
            }
            return new LocalizedString(name, string.Format(name, arguments), resourceNotFound: true);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
        => _store.Resources.Select(kvp => new LocalizedString(kvp.Key, kvp.Value, resourceNotFound: false));
}
