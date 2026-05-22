// Copyright (c) Jan Skoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.
//
// Login UI Redesign + Multi-language — Localization manifest + startup validator.
// Requirements validated: 5.12, 11.7.
//
// This file declares the canonical set of localization keys that the redesigned
// login flow expects to find in resx files for every supported UI culture, plus
// a fire-and-forget startup scanner that emits one Warning per missing
// (ResourceType, Key, Culture) tuple under logger category "Localization".
//
// The marker types defined below (Login, Verify, _PhoneRequestPanel,
// AccountController) live in the canonical view/controller namespaces so that
// the default IStringLocalizerFactory resolution (which builds the resource
// base name from RootNamespace + ResourcePath + relative type FullName) maps
// each marker to the matching .resx file on disk, e.g.
//
//   typeof(Views.Account.Login)
//     -> Resources/Views/Account/Login.{culture}.resx
//   typeof(Controllers.PhoneLoginController)  (real, non-generic)
//     -> Resources/Controllers/PhoneLoginController.{culture}.resx
//
// Generic types cannot be passed to IStringLocalizerFactory.Create directly
// (their FullName carries the arity backtick), so a non-generic, internal,
// sealed [NonController] marker class named "AccountController" coexists with
// the existing public generic AccountController<TUser, TKey> by virtue of
// distinct CLR arity. Internal+[NonController] guarantees the marker is never
// discovered as an MVC controller.

using System;
using System.Collections.Generic;
using System.Globalization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Helpers.Localization
{
    /// <summary>
    /// One entry in <see cref="LocalizationManifest.Entries"/>. Pairs a resource
    /// marker type with a localization key that must resolve in every supported
    /// UI culture.
    /// </summary>
    public sealed record LocalizationManifestEntry(Type ResourceType, string Key);

    /// <summary>
    /// Single source of truth for which localization keys must exist in which
    /// resx files for the redesigned login flow. Adding a new culture later
    /// requires no code change here — the validator simply scans every entry
    /// against every supported culture passed to
    /// <see cref="LocalizationManifestValidator.ValidateAtStartup"/>.
    /// </summary>
    public static class LocalizationManifest
    {
        /// <summary>
        /// Read-only list of every (resource type, key) tuple referenced by the
        /// login redesign. Keys are grouped by resource file for readability;
        /// order is not significant for the validator.
        /// </summary>
        public static readonly IReadOnlyList<LocalizationManifestEntry> Entries = new LocalizationManifestEntry[]
        {
            // Login_Page redesign keys — Resources/Views/Account/Login.{culture}.resx
            new(typeof(Views.Account.Login), "Login.Title"),
            new(typeof(Views.Account.Login), "Login.Subtitle"),
            new(typeof(Views.Account.Login), "Login.TenantPillLabel"),
            new(typeof(Views.Account.Login), "Login.TenantPillAriaLabel"),
            new(typeof(Views.Account.Login), "Login.Nav.Products"),
            new(typeof(Views.Account.Login), "Login.Nav.Features"),
            new(typeof(Views.Account.Login), "Login.Nav.Pricing"),
            new(typeof(Views.Account.Login), "Login.HeaderCtaLogin"),
            new(typeof(Views.Account.Login), "Login.TermsNotice"),
            new(typeof(Views.Account.Login), "Login.TermsLink"),
            new(typeof(Views.Account.Login), "Login.PrivacyLink"),
            new(typeof(Views.Account.Login), "Login.SupportLink"),
            new(typeof(Views.Account.Login), "Login.Forgot"),
            new(typeof(Views.Account.Login), "Login.SignUp"),
            new(typeof(Views.Account.Login), "Login.NoAccount"),
            new(typeof(Views.Account.Login), "Login.Or"),

            // Phone_Verify_Page keys — Resources/Views/Account/LoginWithPhone/Verify.{culture}.resx
            new(typeof(Views.Account.LoginWithPhone.Verify), "LoginWithPhone.Verify.Title"),
            new(typeof(Views.Account.LoginWithPhone.Verify), "LoginWithPhone.Verify.Subtitle"),
            new(typeof(Views.Account.LoginWithPhone.Verify), "LoginWithPhone.OtpLabel"),
            new(typeof(Views.Account.LoginWithPhone.Verify), "LoginWithPhone.MaskedPhonePrefix"),
            new(typeof(Views.Account.LoginWithPhone.Verify), "LoginWithPhone.VerifySubmit"),
            new(typeof(Views.Account.LoginWithPhone.Verify), "LoginWithPhone.Resend"),
            new(typeof(Views.Account.LoginWithPhone.Verify), "LoginWithPhone.BackToLogin"),
            new(typeof(Views.Account.LoginWithPhone.Verify), "LoginWithPhone.GenericVerifyError"),
            new(typeof(Views.Account.LoginWithPhone.Verify), "LoginWithPhone.TabPhone"),

            // _PhoneRequestPanel keys — Resources/Views/Shared/_PhoneRequestPanel.{culture}.resx
            new(typeof(Views.Shared._PhoneRequestPanel), "LoginWithPhone.PhoneLabel"),
            new(typeof(Views.Shared._PhoneRequestPanel), "LoginWithPhone.PhonePlaceholder"),
            new(typeof(Views.Shared._PhoneRequestPanel), "LoginWithPhone.RequestSubmit"),
            new(typeof(Views.Shared._PhoneRequestPanel), "LoginWithPhone.GenericError"),
            new(typeof(Views.Shared._PhoneRequestPanel), "LoginWithPhone.TabsLabel"),
            new(typeof(Views.Shared._PhoneRequestPanel), "LoginWithPhone.TabAccount"),
            new(typeof(Views.Shared._PhoneRequestPanel), "LoginWithPhone.TabPhone"),

            // AccountController keys — Resources/Controllers/AccountController.{culture}.resx
            // Resolved via the non-generic marker class declared below; coexists
            // with the public generic AccountController<TUser, TKey> by arity.
            new(typeof(Controllers.AccountController), "ConfirmEmailBody"),
            new(typeof(Controllers.AccountController), "ConfirmEmailTitle"),
            new(typeof(Controllers.AccountController), "EmailNotFound"),
            new(typeof(Controllers.AccountController), "ErrorExternalProvider"),
            new(typeof(Controllers.AccountController), "InvalidAuthenticatorCode"),
            new(typeof(Controllers.AccountController), "InvalidRecoveryCode"),
            new(typeof(Controllers.AccountController), "ResetPasswordBody"),
            new(typeof(Controllers.AccountController), "ResetPasswordTitle"),
            new(typeof(Controllers.AccountController), "Unable2FA"),

            // PhoneLoginController error keys — Resources/Controllers/PhoneLoginController.{culture}.resx
            // Uses the real (non-generic) controller type directly.
            new(typeof(Controllers.PhoneLoginController), "Generic_Request_Error"),
            new(typeof(Controllers.PhoneLoginController), "Generic_Verify_Error"),
        };
    }

    /// <summary>
    /// Stateless startup scanner that walks <see cref="LocalizationManifest.Entries"/>
    /// across every supported UI culture and emits one
    /// <see cref="LogLevel.Warning"/> per missing (ResourceType, Key, Culture)
    /// tuple under logger category <c>"Localization"</c>. Read-only, fire-and-forget,
    /// never throws, never blocks startup. Deduplicates across invocations via
    /// a process-wide static <see cref="HashSet{T}"/> so repeated calls do not
    /// produce repeat warnings.
    /// </summary>
    public static class LocalizationManifestValidator
    {
        /// <summary>
        /// Process-wide deduplication set keyed by (ResourceType, Key, CultureName).
        /// Guarded by <see cref="_gate"/> for thread-safe Add.
        /// </summary>
        private static readonly HashSet<(Type ResourceType, string Key, string Culture)> _warnedTuples
            = new HashSet<(Type, string, string)>();

        private static readonly object _gate = new object();

        /// <summary>
        /// Scans every entry in <see cref="LocalizationManifest.Entries"/> against
        /// every culture in <paramref name="supportedUICultures"/>. For each
        /// <see cref="LocalizedString"/> whose <see cref="LocalizedString.ResourceNotFound"/>
        /// is <c>true</c>, emits a single <see cref="LogLevel.Warning"/> via the
        /// supplied <paramref name="logger"/> (whose category should be
        /// <c>"Localization"</c>). Subsequent invocations skip tuples already
        /// warned about so logs do not repeat.
        /// </summary>
        /// <remarks>
        /// All exceptions are swallowed: a missing service, a malformed culture,
        /// or any other failure must never affect application startup or request
        /// handling. The scan is intended to be invoked once from
        /// <c>Startup.Configure</c> after <c>app.UseRequestLocalization(...)</c>.
        /// </remarks>
        public static void ValidateAtStartup(
            IServiceProvider services,
            IEnumerable<CultureInfo> supportedUICultures,
            ILogger logger)
        {
            if (services == null || supportedUICultures == null || logger == null)
            {
                return;
            }

            try
            {
                var factory = services.GetService<IStringLocalizerFactory>();
                if (factory == null)
                {
                    return;
                }

                foreach (var culture in supportedUICultures)
                {
                    if (culture == null)
                    {
                        continue;
                    }

                    var previousUiCulture = CultureInfo.CurrentUICulture;
                    var previousCulture = CultureInfo.CurrentCulture;

                    try
                    {
                        // Switch ambient culture so the resource manager picks the
                        // correct .resx file for this iteration.
                        CultureInfo.CurrentUICulture = culture;
                        CultureInfo.CurrentCulture = culture;

                        foreach (var entry in LocalizationManifest.Entries)
                        {
                            if (entry == null || entry.ResourceType == null || string.IsNullOrEmpty(entry.Key))
                            {
                                continue;
                            }

                            try
                            {
                                var localizer = factory.Create(entry.ResourceType);
                                if (localizer == null)
                                {
                                    continue;
                                }

                                var localized = localizer[entry.Key];
                                if (localized != null && localized.ResourceNotFound)
                                {
                                    EmitWarningOnce(logger, entry.ResourceType, entry.Key, culture.Name);
                                }
                            }
                            catch
                            {
                                // Per-entry failures must not abort the scan.
                            }
                        }
                    }
                    finally
                    {
                        CultureInfo.CurrentUICulture = previousUiCulture;
                        CultureInfo.CurrentCulture = previousCulture;
                    }
                }
            }
            catch
            {
                // Top-level guard: never throw out of startup.
            }
        }

        private static void EmitWarningOnce(ILogger logger, Type resourceType, string key, string cultureName)
        {
            var tuple = (resourceType, key, cultureName);

            bool added;
            lock (_gate)
            {
                added = _warnedTuples.Add(tuple);
            }

            if (!added)
            {
                return;
            }

            try
            {
                logger.LogWarning(
                    "Localization key missing. ResourceType={ResourceType}, Key={Key}, Culture={Culture}",
                    resourceType.FullName,
                    key,
                    cultureName);
            }
            catch
            {
                // A logger that throws must not break startup.
            }
        }
    }
}

// =====================================================================
// Marker types for IStringLocalizerFactory.Create(Type) resolution.
//
// Each marker's FullName matches the canonical view/controller path so the
// default ResourceManagerStringLocalizerFactory builds the correct resource
// base name. Markers are internal and sealed; the controller marker also
// carries [NonController] as defense-in-depth so MVC's controller discovery
// never picks them up.
// =====================================================================

namespace Skoruba.Duende.IdentityServer.STS.Identity.Views.Account
{
    /// <summary>
    /// Marker for <c>Resources/Views/Account/Login.{culture}.resx</c>. Used only
    /// by <see cref="Helpers.Localization.LocalizationManifest"/> entries.
    /// </summary>
    internal sealed class Login
    {
        private Login() { }
    }
}

namespace Skoruba.Duende.IdentityServer.STS.Identity.Views.Account.LoginWithPhone
{
    /// <summary>
    /// Marker for <c>Resources/Views/Account/LoginWithPhone/Verify.{culture}.resx</c>.
    /// Used only by <see cref="Helpers.Localization.LocalizationManifest"/> entries.
    /// </summary>
    internal sealed class Verify
    {
        private Verify() { }
    }
}

namespace Skoruba.Duende.IdentityServer.STS.Identity.Views.Shared
{
    /// <summary>
    /// Marker for <c>Resources/Views/Shared/_PhoneRequestPanel.{culture}.resx</c>.
    /// Used only by <see cref="Helpers.Localization.LocalizationManifest"/> entries.
    /// </summary>
    internal sealed class _PhoneRequestPanel
    {
        private _PhoneRequestPanel() { }
    }
}

namespace Skoruba.Duende.IdentityServer.STS.Identity.Controllers
{
    /// <summary>
    /// Non-generic, internal, [NonController] marker for
    /// <c>Resources/Controllers/AccountController.{culture}.resx</c>. Coexists
    /// with the public generic <c>AccountController&lt;TUser, TKey&gt;</c> by virtue
    /// of distinct CLR arity (0 vs 2). Used only by
    /// <see cref="Helpers.Localization.LocalizationManifest"/> entries; never
    /// instantiated and never discovered as an MVC controller.
    /// </summary>
    [NonController]
    internal sealed class AccountController
    {
        private AccountController() { }
    }
}
