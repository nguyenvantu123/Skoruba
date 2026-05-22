// Copyright (c) Jan Skoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Claims;
using NetIPNetwork = System.Net.IPNetwork;
using Duende.IdentityServer;
using Duende.IdentityServer.Configuration;
using Duende.IdentityServer.EntityFramework.Storage;
using IdentityModel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Tokens;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.Configuration;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.MySql;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.PostgreSQL;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.SqlServer;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Helpers;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Interfaces;
using Skoruba.Duende.IdentityServer.Shared.Configuration.Authentication;
using Skoruba.Duende.IdentityServer.Shared.Configuration.Configuration.Identity;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration.ApplicationParts;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration.Constants;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration.Interfaces;
using Skoruba.Duende.IdentityServer.STS.Identity.Helpers.Localization;
using Skoruba.Duende.IdentityServer.STS.Identity.Services;
using TenantInfrastructure.Identity;
using Configuration = Skoruba.Duende.IdentityServer.STS.Identity.Configuration;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.AspNetCore.Authorization;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Helpers
{
    public static class StartupHelpers
    {
        /// <summary>
        /// Register services for MVC and localization including available languages
        /// </summary>
        /// <param name="services"></param>
        public static IMvcBuilder AddMvcWithLocalization<TUser, TKey>(this IServiceCollection services, IConfiguration configuration)
            where TUser : IdentityUser<TKey>
            where TKey : IEquatable<TKey>
        {
            services.AddLocalization(opts => { opts.ResourcesPath = ConfigurationConsts.ResourcesPath; });

            services.TryAddTransient(typeof(IGenericControllerLocalizer<>), typeof(GenericControllerLocalizer<>));

            var mvcBuilder = services.AddControllersWithViews(o =>
                {
                    o.Conventions.Add(new GenericControllerRouteConvention());
                })
                .AddViewLocalization(
                    LanguageViewLocationExpanderFormat.Suffix,
                    opts => { opts.ResourcesPath = ConfigurationConsts.ResourcesPath; })
                .AddDataAnnotationsLocalization()
                .ConfigureApplicationPartManager(m =>
                {
                    m.FeatureProviders.Add(new GenericTypeControllerFeatureProvider<TUser, TKey>());
                });

            var cultureConfiguration = configuration.GetSection(nameof(CultureConfiguration)).Get<CultureConfiguration>();

            // Centralize culture/default resolution in a pure utility so this method stays declarative
            // and the logic is unit/property-testable. See Requirements 7.1, 7.2, 7.3, 7.7.
            var resolvedCultures = CultureConfigurationResolver.Resolve(cultureConfiguration);

            // Surface unparseable culture codes to operations at startup (Requirement 7.7).
            // Prefer ILogger; if logging is not yet wired up, fall back to Console.Error per the documented fallback.
            if (resolvedCultures.InvalidCultureCodes.Count > 0)
            {
                ILogger? cultureLogger = null;
                try
                {
                    var loggerFactory = services.BuildServiceProvider().GetService<ILoggerFactory>();
                    cultureLogger = loggerFactory?.CreateLogger("Skoruba.Duende.IdentityServer.STS.Identity.Localization");
                }
                catch
                {
                    // Logger acquisition is best-effort at startup. Fallback below ensures the operator still sees the error.
                    cultureLogger = null;
                }

                foreach (var invalidCode in resolvedCultures.InvalidCultureCodes)
                {
                    if (cultureLogger != null)
                    {
                        cultureLogger.LogError(
                            "Invalid culture code in CultureConfiguration:Cultures: '{InvalidCultureCode}'. The culture has been skipped.",
                            invalidCode);
                    }
                    else
                    {
                        // Documented fallback per Requirement 7.7 when no ILoggerFactory is available at startup.
                        Console.Error.WriteLine(
                            $"[Localization] Invalid culture code in CultureConfiguration:Cultures: '{invalidCode}'. The culture has been skipped.");
                    }
                }
            }

            services.Configure<RequestLocalizationOptions>(opts =>
            {
                opts.DefaultRequestCulture = new RequestCulture(resolvedCultures.DefaultCulture);
                opts.SupportedCultures = resolvedCultures.SupportedCultures.ToList();
                opts.SupportedUICultures = resolvedCultures.SupportedCultures.ToList();

                // Provider order: OIDC ui_locales > query string > cookie > Accept-Language.
                // The OIDC bridge runs first so an authorize-time `ui_locales` request from
                // a relying party wins over any inherited/sticky preference. Query string
                // (`?culture=` / `?ui-culture=`) remains second for non-OIDC callers.
                // Cookie keeps the user's choice across pages within the session.
                // Accept-Language is the final fallback driven by browser preferences.
                opts.RequestCultureProviders = new List<IRequestCultureProvider>
                {
                    new OidcUiLocalesRequestCultureProvider(),
                    new QueryStringRequestCultureProvider(),
                    new CookieRequestCultureProvider(),
                    new AcceptLanguageHeaderRequestCultureProvider()
                };
            });

            return mvcBuilder;
        }

        /// <summary>
        /// Using of Forwarded Headers and Referrer Policy
        /// </summary>
        /// <param name="app"></param>
        /// <param name="configuration"></param>
        public static void UseSecurityHeaders(this IApplicationBuilder app, IConfiguration configuration)
        {
            var forwardedHeadersConfig = configuration.GetSection(ConfigurationConsts.ForwardedHeadersConfigurationKey)
                .Get<Configuration.ForwardedHeadersConfiguration>() ?? new Configuration.ForwardedHeadersConfiguration();

            if (forwardedHeadersConfig.Enabled)
            {
                var forwardingOptions = new ForwardedHeadersOptions()
                {
                    ForwardedHeaders = ForwardedHeaders.All,
                    ForwardLimit = forwardedHeadersConfig.ForwardLimit
                };

                if (forwardedHeadersConfig.AllowAll)
                {
                    // Development mode: allow all proxies and networks (insecure)
                    forwardingOptions.KnownIPNetworks.Clear();
                    forwardingOptions.KnownProxies.Clear();
                }
                else
                {
                    // Production mode: only trust configured proxies and networks
                    if (forwardedHeadersConfig.KnownProxies != null && forwardedHeadersConfig.KnownProxies.Count > 0)
                    {
                        forwardingOptions.KnownProxies.Clear();
                        foreach (var proxy in forwardedHeadersConfig.KnownProxies)
                        {
                            if (System.Net.IPAddress.TryParse(proxy, out var ipAddress))
                            {
                                forwardingOptions.KnownProxies.Add(ipAddress);
                            }
                        }
                    }

                    if (forwardedHeadersConfig.KnownNetworks != null && forwardedHeadersConfig.KnownNetworks.Count > 0)
                    {
                        forwardingOptions.KnownIPNetworks.Clear();
                        foreach (var network in forwardedHeadersConfig.KnownNetworks)
                        {
                            var parts = network.Split('/');
                            if (parts.Length == 2 &&
                                IPAddress.TryParse(parts[0], out var prefix) &&
                                int.TryParse(parts[1], out var prefixLength))
                            {
                                forwardingOptions.KnownIPNetworks.Add(new NetIPNetwork(prefix, prefixLength));
                            }
                        }
                    }

                    // If no proxies or networks configured, don't clear defaults (more secure)
                    // This means it will only trust the loopback by default
                }

                app.UseForwardedHeaders(forwardingOptions);
            }

            app.UseReferrerPolicy(options => options.NoReferrer());

            // CSP Configuration to be able to use external resources
            var cspTrustedDomains = new List<string>();
            configuration.GetSection(ConfigurationConsts.CspTrustedDomainsKey).Bind(cspTrustedDomains);
            if (cspTrustedDomains.Any())
            {
                app.UseCsp(csp =>
                {
                    var imagesSources = new List<string> { "data:" };
                    imagesSources.AddRange(cspTrustedDomains);

                    csp.ImageSources(options =>
                    {
                        options.SelfSrc = true;
                        options.CustomSources = imagesSources;
                        options.Enabled = true;
                    });
                    csp.FontSources(options =>
                    {
                        options.SelfSrc = true;
                        options.CustomSources = cspTrustedDomains;
                        options.Enabled = true;
                    });
                    csp.ScriptSources(options =>
                    {
                        options.SelfSrc = true;
                        options.CustomSources = cspTrustedDomains;
                        options.Enabled = true;
                    });
                    csp.StyleSources(options =>
                    {
                        options.SelfSrc = true;
                        options.CustomSources = cspTrustedDomains;
                        options.Enabled = true;
                        options.UnsafeInlineSrc = false;
                    });
                    csp.Sandbox(options =>
                    {
                        options.AllowForms()
                            .AllowSameOrigin()
                            .AllowScripts();
                    });
                    csp.FrameAncestors(option =>
                    {
                        option.NoneSrc = true;
                        option.Enabled = true;
                    });

                    csp.BaseUris(options =>
                    {
                        options.SelfSrc = true;
                        options.Enabled = true;
                    });

                    csp.ObjectSources(options =>
                    {
                        options.NoneSrc = true;
                        options.Enabled = true;
                    });

                    csp.DefaultSources(options =>
                    {
                        options.Enabled = true;
                        options.SelfSrc = true;
                        options.CustomSources = cspTrustedDomains;
                    });
                });
            }

        }

        /// <summary>
        /// Register DbContexts for IdentityServer ConfigurationStore, PersistedGrants, Identity and DataProtection
        /// Configure the connection strings in AppSettings.json
        /// </summary>
        /// <typeparam name="TConfigurationDbContext"></typeparam>
        /// <typeparam name="TPersistedGrantDbContext"></typeparam>
        /// <typeparam name="TIdentityDbContext"></typeparam>
        /// <typeparam name="TDataProtectionDbContext"></typeparam>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        public static void RegisterDbContexts<TIdentityDbContext, TConfigurationDbContext, TPersistedGrantDbContext, TDataProtectionDbContext>(this IServiceCollection services, IConfiguration configuration)
            where TIdentityDbContext : DbContext
            where TPersistedGrantDbContext : DbContext, IAdminPersistedGrantDbContext
            where TConfigurationDbContext : DbContext, IAdminConfigurationDbContext
            where TDataProtectionDbContext : DbContext, IDataProtectionKeyContext
        {
            var databaseProvider = configuration.GetSection(nameof(DatabaseProviderConfiguration)).Get<DatabaseProviderConfiguration>();
            var identityConnectionString = configuration.GetConnectionString(ConfigurationConsts.IdentityDbConnectionStringKey);
            var configurationConnectionString = configuration.GetConnectionString(ConfigurationConsts.ConfigurationDbConnectionStringKey);
            var persistedGrantsConnectionString = configuration.GetConnectionString(ConfigurationConsts.PersistedGrantDbConnectionStringKey);
            var dataProtectionConnectionString = configuration.GetConnectionString(ConfigurationConsts.DataProtectionDbConnectionStringKey);

            switch (databaseProvider.ProviderType)
            {
                case DatabaseProviderType.SqlServer:
                    services.RegisterSqlServerDbContexts<TIdentityDbContext, TConfigurationDbContext, TPersistedGrantDbContext, TDataProtectionDbContext>(identityConnectionString, configurationConnectionString, persistedGrantsConnectionString, dataProtectionConnectionString);
                    break;
                case DatabaseProviderType.PostgreSQL:
                    services.RegisterNpgSqlDbContexts<TIdentityDbContext, TConfigurationDbContext, TPersistedGrantDbContext, TDataProtectionDbContext>(identityConnectionString, configurationConnectionString, persistedGrantsConnectionString, dataProtectionConnectionString);
                    break;
                case DatabaseProviderType.MySql:
                    services.RegisterMySqlDbContexts<TIdentityDbContext, TConfigurationDbContext, TPersistedGrantDbContext, TDataProtectionDbContext>(identityConnectionString, configurationConnectionString, persistedGrantsConnectionString, dataProtectionConnectionString);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(databaseProvider.ProviderType), $@"The value needs to be one of {string.Join(", ", Enum.GetNames(typeof(DatabaseProviderType)))}.");
            }
        }

        /// <summary>
        /// Register InMemory DbContexts for IdentityServer ConfigurationStore, PersistedGrants, Identity and DataProtection
        /// Configure the connection strings in AppSettings.json
        /// </summary>
        /// <typeparam name="TConfigurationDbContext"></typeparam>
        /// <typeparam name="TPersistedGrantDbContext"></typeparam>
        /// <typeparam name="TIdentityDbContext"></typeparam>
        /// <param name="services"></param>
        public static void RegisterDbContextsStaging<TIdentityDbContext, TConfigurationDbContext, TPersistedGrantDbContext, TDataProtectionDbContext>(
            this IServiceCollection services)
            where TIdentityDbContext : DbContext
            where TPersistedGrantDbContext : DbContext, IAdminPersistedGrantDbContext
            where TConfigurationDbContext : DbContext, IAdminConfigurationDbContext
            where TDataProtectionDbContext : DbContext, IDataProtectionKeyContext
        {
            var identityDatabaseName = Guid.NewGuid().ToString();
            services.AddDbContext<TIdentityDbContext>(optionsBuilder => optionsBuilder.UseInMemoryDatabase(identityDatabaseName));

            var configurationDatabaseName = Guid.NewGuid().ToString();
            var operationalDatabaseName = Guid.NewGuid().ToString();
            var dataProtectionDatabaseName = Guid.NewGuid().ToString();

            services.AddConfigurationDbContext<TConfigurationDbContext>(options =>
            {
                options.ConfigureDbContext = b => b.UseInMemoryDatabase(configurationDatabaseName);
            });

            services.AddOperationalDbContext<TPersistedGrantDbContext>(options =>
            {
                options.ConfigureDbContext = b => b.UseInMemoryDatabase(operationalDatabaseName);
            });

            services.AddDbContext<TDataProtectionDbContext>(options =>
            {
                options.UseInMemoryDatabase(dataProtectionDatabaseName);
            });
        }

        /// <summary>
        /// Add services for authentication, including Identity model, Duende IdentityServer and external providers
        /// </summary>
        /// <typeparam name="TIdentityDbContext">DbContext for Identity</typeparam>
        /// <typeparam name="TUserIdentity">User Identity class</typeparam>
        /// <typeparam name="TUserIdentityRole">User Identity Role class</typeparam>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        public static void AddAuthenticationServices<TIdentityDbContext, TUserIdentity, TUserIdentityRole>(this IServiceCollection services, IConfiguration configuration) where TIdentityDbContext : DbContext
            where TUserIdentity : class
            where TUserIdentityRole : class
        {
            var loginConfiguration = GetLoginConfiguration(configuration);
            var registrationConfiguration = GetRegistrationConfiguration(configuration);
            var identityOptions = configuration.GetSection(nameof(IdentityOptions)).Get<IdentityOptions>();

            services
                .AddSingleton(registrationConfiguration)
                .AddSingleton(loginConfiguration)
                .AddSingleton(identityOptions)
                .AddScoped<ApplicationSignInManager<TUserIdentity>>()
                .AddScoped<UserResolver<TUserIdentity>>()
                .AddIdentity<TUserIdentity, TUserIdentityRole>(options => configuration.GetSection(nameof(IdentityOptions)).Bind(options))
                .AddEntityFrameworkStores<TIdentityDbContext>()
                .AddDefaultTokenProviders();

            var persistentLoginDuration = TimeSpan.FromDays(Math.Max(1, loginConfiguration.PersistentLoginDays));

            services.ConfigureApplicationCookie(options =>
            {
                if (loginConfiguration.PersistLogin)
                {
                    options.SlidingExpiration = true;
                    options.ExpireTimeSpan = persistentLoginDuration;
                }

                options.Events.OnSigningIn = context =>
                {
                    if (!loginConfiguration.PersistLogin)
                    {
                        return Task.CompletedTask;
                    }

                    context.Properties.IsPersistent = true;
                    context.Properties.AllowRefresh = true;

                    if (!context.Properties.ExpiresUtc.HasValue)
                    {
                        context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.Add(persistentLoginDuration);
                    }

                    return Task.CompletedTask;
                };
            });

            services.Configure<CookiePolicyOptions>(options =>
            {
                options.MinimumSameSitePolicy = SameSiteMode.Unspecified;
                options.Secure = CookieSecurePolicy.SameAsRequest;
                options.OnAppendCookie = cookieContext =>
                    AuthenticationHelpers.CheckSameSite(cookieContext.Context, cookieContext.CookieOptions);
                options.OnDeleteCookie = cookieContext =>
                    AuthenticationHelpers.CheckSameSite(cookieContext.Context, cookieContext.CookieOptions);
            });



            services.Configure<IISOptions>(iis =>
            {
                iis.AuthenticationDisplayName = "Windows";
                iis.AutomaticAuthentication = false;
            });

            var authenticationBuilder = services.AddAuthentication();
            authenticationBuilder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var tenantRegistryApiConfiguration = configuration.GetSection(nameof(TenantRegistryApiConfiguration)).Get<TenantRegistryApiConfiguration>() ?? new TenantRegistryApiConfiguration();
                var authority = (tenantRegistryApiConfiguration.Authority ?? string.Empty).Trim().TrimEnd('/');
                var requireHttpsMetadata = tenantRegistryApiConfiguration.RequireHttpsMetadata;
                if (string.IsNullOrWhiteSpace(authority))
                {
                    authority = "https://sts.dev.localhost:5001";
                    requireHttpsMetadata = false;
                    Console.WriteLine($"[TenantRegistryApi] Missing configuration value: {nameof(TenantRegistryApiConfiguration)}:{nameof(TenantRegistryApiConfiguration.Authority)}. Falling back to '{authority}'.");
                }

                options.Authority = authority;
                options.RequireHttpsMetadata = requireHttpsMetadata;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateAudience = false,
                    ValidateIssuer = true,
                    ValidIssuer = authority,
                    NameClaimType = JwtClaimTypes.Name,
                    RoleClaimType = JwtClaimTypes.Role
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var hasBearerToken = context.Request.Headers.ContainsKey("Authorization");
                        Console.WriteLine($"[TenantRegistryApi] Bearer header received: {hasBearerToken}");
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        var issuer = context.Principal?.FindFirst("iss")?.Value;
                        var tenantKey = context.Principal?.FindFirst(TenantClaimTypes.TenantKey)?.Value;
                        Console.WriteLine($"[TenantRegistryApi] Token validated. Issuer={issuer}, TenantKey={tenantKey}");
                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        Console.WriteLine($"[TenantRegistryApi] Authentication failed: {context.Exception}");
                        return Task.CompletedTask;
                    },
                    OnChallenge = context =>
                    {
                        Console.WriteLine($"[TenantRegistryApi] Challenge triggered. Error={context.Error}, Description={context.ErrorDescription}");
                        return Task.CompletedTask;
                    }
                };

                var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
                if (isDevelopment && IsLocalDevelopmentHttpsUri(authority))
                {
                    options.BackchannelHttpHandler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    };
                }
            });

            AddExternalProviders(authenticationBuilder, configuration);
        }

        private static LoginConfiguration GetLoginConfiguration(IConfiguration configuration)
        {
            var loginConfiguration = configuration.GetSection(nameof(LoginConfiguration)).Get<LoginConfiguration>();
            return loginConfiguration ?? new LoginConfiguration();
        }

        private static RegisterConfiguration GetRegistrationConfiguration(IConfiguration configuration)
        {
            var registerConfiguration = configuration.GetSection(nameof(RegisterConfiguration)).Get<RegisterConfiguration>();
            return registerConfiguration ?? new RegisterConfiguration();
        }

        private static bool IsLocalDevelopmentHttpsUri(string? uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri) ||
                parsedUri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            return parsedUri.IsLoopback ||
                   string.Equals(parsedUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   parsedUri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        }

        public static IIdentityServerBuilder AddIdentityServer<TConfigurationDbContext, TPersistedGrantDbContext, TUserIdentity>(this IServiceCollection services, IConfiguration configuration)
            where TPersistedGrantDbContext : DbContext, IAdminPersistedGrantDbContext
            where TConfigurationDbContext : DbContext, IAdminConfigurationDbContext
            where TUserIdentity : class
        {
            var configurationSection = configuration.GetSection(nameof(IdentityServerOptions));

            var identityServerOptions = configurationSection.Get<IdentityServerOptions>();

            var builder = services.AddIdentityServer(options =>
                {
                    configurationSection.Bind(options);

                    options.DynamicProviders.SignInScheme = IdentityConstants.ExternalScheme;
                    options.DynamicProviders.SignOutScheme = IdentityConstants.ApplicationScheme;
                })
                .AddInMemoryCaching()
                .AddConfigurationStore<TConfigurationDbContext>()
                .AddConfigurationStoreCache()
                .AddOperationalStore<TPersistedGrantDbContext>()
                .AddAspNetIdentity<TUserIdentity>()
                .AddProfileService<TenantProfileService>();

            services.ConfigureOptions<OpenIdClaimsMappingConfig>();

            var keyManagementEnabled = identityServerOptions.KeyManagement?.Enabled ?? true;
            if (!keyManagementEnabled)
            {
                builder.AddCustomSigningCredential(configuration);
                builder.AddCustomValidationKey(configuration);
            }

            builder.AddExtensionGrantValidator<DelegationGrantValidator>();

            var serverSideSessionsConfig = configuration.GetSection(Configuration.ServerSideSessionsConfiguration.SectionName).Get<Configuration.ServerSideSessionsConfiguration>() ?? new Configuration.ServerSideSessionsConfiguration();
            if (serverSideSessionsConfig.Enabled)
            {
                builder.AddServerSideSessions();
                services.Configure<IdentityServerOptions>(options =>
                {
                    options.ServerSideSessions.UserDisplayNameClaimType = JwtClaimTypes.Name;
                    options.ServerSideSessions.RemoveExpiredSessions = true;
                    options.ServerSideSessions.ExpiredSessionsTriggerBackchannelLogout = true;
                });
            }

            return builder;
        }

        /// <summary>
        /// Add external providers
        /// </summary>
        /// <param name="authenticationBuilder"></param>
        /// <param name="configuration"></param>
        private static void AddExternalProviders(AuthenticationBuilder authenticationBuilder,
            IConfiguration configuration)
        {
            var externalProviderConfiguration = configuration.GetSection(nameof(ExternalProvidersConfiguration)).Get<ExternalProvidersConfiguration>();

            if (externalProviderConfiguration.UseGitHubProvider)
            {
                authenticationBuilder.AddGitHub(options =>
                {
                    options.ClientId = externalProviderConfiguration.GitHubClientId;
                    options.ClientSecret = externalProviderConfiguration.GitHubClientSecret;
                    options.CallbackPath = externalProviderConfiguration.GitHubCallbackPath;
                    options.Scope.Add("user:email");
                });
            }

            if (externalProviderConfiguration.UseAzureAdProvider)
            {
                authenticationBuilder.AddMicrosoftIdentityWebApp(options =>
                {
                    options.ClientSecret = externalProviderConfiguration.AzureAdSecret;
                    options.ClientId = externalProviderConfiguration.AzureAdClientId;
                    options.TenantId = externalProviderConfiguration.AzureAdTenantId;
                    options.Instance = externalProviderConfiguration.AzureInstance;
                    options.Domain = externalProviderConfiguration.AzureDomain;
                    options.CallbackPath = externalProviderConfiguration.AzureAdCallbackPath;
                }, cookieScheme: null);
            }
        }




        /// <summary>
        /// Register middleware for localization
        /// </summary>
        /// <param name="app"></param>
        public static void UseMvcLocalizationServices(this IApplicationBuilder app)
        {
            var options = app.ApplicationServices.GetService<IOptions<RequestLocalizationOptions>>();
            app.UseRequestLocalization(options.Value);

            // OIDC ui_locales bridge — when /connect/authorize (or any handler) resolves
            // a culture from the `ui_locales` parameter, persist that choice to the
            // standard `.AspNetCore.Culture` cookie so subsequent navigations within the
            // STS host (the Login_Page, Phone_Verify_Page, error pages) keep the language
            // even after the relying party's authorize redirect has consumed the
            // ui_locales parameter. Cookie attributes mirror what `HomeController.SetLanguage`
            // emits (1-year expiry, Path=/, IsEssential=true).
            app.Use(async (ctx, next) =>
            {
                try
                {
                    var feature = ctx.Features.Get<IRequestCultureFeature>();
                    var providerType = feature?.Provider?.GetType();
                    if (providerType == typeof(OidcUiLocalesRequestCultureProvider) && feature is not null)
                    {
                        var existing = ctx.Request.Cookies[CookieRequestCultureProvider.DefaultCookieName];
                        var desired = CookieRequestCultureProvider.MakeCookieValue(feature.RequestCulture);
                        if (!string.Equals(existing, desired, StringComparison.Ordinal))
                        {
                            ctx.Response.Cookies.Append(
                                CookieRequestCultureProvider.DefaultCookieName,
                                desired,
                                new CookieOptions
                                {
                                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                                    IsEssential = true,
                                    Path = "/",
                                    HttpOnly = false,
                                    SameSite = SameSiteMode.Lax,
                                    Secure = ctx.Request.IsHttps
                                });
                        }
                    }
                }
                catch
                {
                    // Cookie persistence is best-effort. A failure here must not affect
                    // the request — the user's culture for *this* request is already set
                    // by the provider; only the sticky-across-pages effect is lost.
                }

                await next();
            });

            // Login UI Redesign + Multi-language — Requirement 5.12: scan the localization
            // manifest once at startup and emit one Warning per missing (resource type, key,
            // culture) tuple under logger category "Localization". The validator itself
            // swallows every exception internally; the wiring here is also try/catch-guarded
            // so that a misconfigured DI graph or logger factory cannot crash the host.
            try
            {
                var services = app.ApplicationServices;
                var supportedUICultures = options?.Value?.SupportedUICultures;
                var loggerFactory = services.GetService<ILoggerFactory>();

                if (services != null && supportedUICultures != null && loggerFactory != null)
                {
                    var manifestLogger = loggerFactory.CreateLogger("Localization");
                    LocalizationManifestValidator.ValidateAtStartup(services, supportedUICultures, manifestLogger);
                }
            }
            catch
            {
                // Defense-in-depth: localization manifest validation must never block startup
                // or affect request handling. The validator already swallows internally; this
                // outer guard protects the surrounding wiring (service resolution, logger
                // factory creation) from ever surfacing an exception.
            }
        }

        /// <summary>
        /// Add authorization policies
        /// </summary>
        /// <param name="services"></param>
        /// <param name="rootConfiguration"></param>
        public static void AddAuthorizationPolicies(this IServiceCollection services,
                IRootConfiguration rootConfiguration)
        {
            services.AddAuthorization(options =>
            {
                options.AddPolicy(AuthorizationConsts.AdministrationPolicy,
                    policy => policy.RequireRole(rootConfiguration.AdminConfiguration.AdministrationRole));
                var tenantAdminAssertion = new Func<AuthorizationHandlerContext, bool>(context =>
                {
                    var tenantAdminRole = rootConfiguration.AdminConfiguration.TenantAdminRole;
                    if (string.IsNullOrWhiteSpace(tenantAdminRole))
                    {
                        return false;
                    }

                    var tenantKey = context.User.FindFirst(TenantClaimTypes.TenantKey)?.Value;
                    if (string.IsNullOrWhiteSpace(tenantKey))
                    {
                        return false;
                    }

                    return context.User.HasClaim(c =>
                        (c.Type == JwtClaimTypes.Role || c.Type == ClaimTypes.Role) &&
                        c.Value == tenantAdminRole);
                });

                options.AddPolicy(AuthorizationConsts.TenantAdminApiPolicy,
                    policy =>
                    {
                        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                        policy.RequireAuthenticatedUser();
                        policy.RequireAssertion(tenantAdminAssertion);
                    });

                options.AddPolicy(AuthorizationConsts.TenantAdminTenantRegistryPolicy,
                    policy =>
                    {
                        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                        policy.RequireAuthenticatedUser();
                        policy.RequireAssertion(tenantAdminAssertion);
                    });
            });
        }

        public static void AddIdSHealthChecks<TConfigurationDbContext, TPersistedGrantDbContext, TIdentityDbContext, TDataProtectionDbContext>(this IServiceCollection services, IConfiguration configuration)
            where TConfigurationDbContext : DbContext, IAdminConfigurationDbContext
            where TPersistedGrantDbContext : DbContext, IAdminPersistedGrantDbContext
            where TIdentityDbContext : DbContext
            where TDataProtectionDbContext : DbContext, IDataProtectionKeyContext
        {
            var configurationDbConnectionString = configuration.GetConnectionString(ConfigurationConsts.ConfigurationDbConnectionStringKey);
            var persistedGrantsDbConnectionString = configuration.GetConnectionString(ConfigurationConsts.PersistedGrantDbConnectionStringKey);
            var identityDbConnectionString = configuration.GetConnectionString(ConfigurationConsts.IdentityDbConnectionStringKey);
            var dataProtectionDbConnectionString = configuration.GetConnectionString(ConfigurationConsts.DataProtectionDbConnectionStringKey);

            var healthChecksBuilder = services.AddHealthChecks()
                .AddDbContextCheck<TConfigurationDbContext>("ConfigurationDbContext")
                .AddDbContextCheck<TPersistedGrantDbContext>("PersistedGrantsDbContext")
                .AddDbContextCheck<TIdentityDbContext>("IdentityDbContext")
                .AddDbContextCheck<TDataProtectionDbContext>("DataProtectionDbContext");

            var serviceProvider = services.BuildServiceProvider();
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
            using (var scope = scopeFactory.CreateScope())
            {
                var configurationTableName = DbContextHelpers.GetEntityTable<TConfigurationDbContext>(scope.ServiceProvider);
                var persistedGrantTableName = DbContextHelpers.GetEntityTable<TPersistedGrantDbContext>(scope.ServiceProvider);
                var identityTableName = DbContextHelpers.GetEntityTable<TIdentityDbContext>(scope.ServiceProvider);
                var dataProtectionTableName = DbContextHelpers.GetEntityTable<TDataProtectionDbContext>(scope.ServiceProvider);

                var databaseProvider = configuration.GetSection(nameof(DatabaseProviderConfiguration)).Get<DatabaseProviderConfiguration>();
                switch (databaseProvider.ProviderType)
                {
                    case DatabaseProviderType.SqlServer:
                        healthChecksBuilder
                            .AddSqlServer(configurationDbConnectionString, name: "ConfigurationDb", healthQuery: $"SELECT TOP 1 * FROM dbo.[{configurationTableName}]")
                            .AddSqlServer(persistedGrantsDbConnectionString, name: "PersistentGrantsDb", healthQuery: $"SELECT TOP 1 * FROM dbo.[{persistedGrantTableName}]")
                            .AddSqlServer(identityDbConnectionString, name: "IdentityDb", healthQuery: $"SELECT TOP 1 * FROM dbo.[{identityTableName}]")
                            .AddSqlServer(dataProtectionDbConnectionString, name: "DataProtectionDb", healthQuery: $"SELECT TOP 1 * FROM dbo.[{dataProtectionTableName}]");
                        break;
                    case DatabaseProviderType.PostgreSQL:
                        healthChecksBuilder
                            .AddNpgSql(configurationDbConnectionString, name: "ConfigurationDb", healthQuery: $"SELECT * FROM \"{configurationTableName}\" LIMIT 1")
                            .AddNpgSql(persistedGrantsDbConnectionString, name: "PersistentGrantsDb", healthQuery: $"SELECT * FROM \"{persistedGrantTableName}\" LIMIT 1")
                            .AddNpgSql(identityDbConnectionString, name: "IdentityDb", healthQuery: $"SELECT * FROM \"{identityTableName}\" LIMIT 1")
                            .AddNpgSql(dataProtectionDbConnectionString, name: "DataProtectionDb", healthQuery: $"SELECT * FROM \"{dataProtectionTableName}\"  LIMIT 1");
                        break;
                    case DatabaseProviderType.MySql:
                        configurationDbConnectionString = NormalizeMySqlConnectionStringForDevelopment(configurationDbConnectionString);
                        persistedGrantsDbConnectionString = NormalizeMySqlConnectionStringForDevelopment(persistedGrantsDbConnectionString);
                        identityDbConnectionString = NormalizeMySqlConnectionStringForDevelopment(identityDbConnectionString);
                        dataProtectionDbConnectionString = NormalizeMySqlConnectionStringForDevelopment(dataProtectionDbConnectionString);

                        healthChecksBuilder
                            .AddMySql(configurationDbConnectionString, name: "ConfigurationDb", healthQuery: $"SELECT * FROM \"{configurationTableName}\" LIMIT 1")
                            .AddMySql(persistedGrantsDbConnectionString, name: "PersistentGrantsDb", healthQuery: $"SELECT * FROM \"{persistedGrantTableName}\" LIMIT 1")
                            .AddMySql(identityDbConnectionString, name: "IdentityDb", healthQuery: $"SELECT * FROM \"{identityTableName}\" LIMIT 1")
                            .AddMySql(dataProtectionDbConnectionString, name: "DataProtectionDb", healthQuery: $"SELECT * FROM \"{dataProtectionTableName}\"  LIMIT 1");
                        break;
                    default:
                        throw new NotImplementedException($"Health checks not defined for database provider {databaseProvider.ProviderType}");
                }
            }
        }

        private static string NormalizeMySqlConnectionStringForDevelopment(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return connectionString;

            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
            if (!isDevelopment)
                return connectionString;

            var parts = connectionString
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(part =>
                {
                    var trimmedPart = part.TrimStart();
                    return !trimmedPart.StartsWith("SslMode=", StringComparison.OrdinalIgnoreCase) &&
                           !trimmedPart.StartsWith("Ssl Mode=", StringComparison.OrdinalIgnoreCase) &&
                           !trimmedPart.StartsWith("AllowPublicKeyRetrieval=", StringComparison.OrdinalIgnoreCase);
                });

            return $"{string.Join(";", parts)};AllowPublicKeyRetrieval=True;SslMode=Disabled";
        }
    }
}



