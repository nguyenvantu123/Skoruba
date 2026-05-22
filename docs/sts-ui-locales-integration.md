# STS Login UI Localization — Integration Guide for Mobile and Web Apps

This guide explains how a relying party (mobile app, single-page web app, server-rendered web app, or anything else that talks OpenID Connect) can pin the language of the Skoruba STS login screens (`/Account/Login`, `/Account/LoginWithPhone/Verify`, error pages, password-reset flow) on a per-request basis.

The STS host supports four mechanisms in priority order:

| Priority | Source                              | When it applies                                                | Sticky across pages? |
| :------: | ----------------------------------- | -------------------------------------------------------------- | :------------------: |
| 1        | OIDC `ui_locales` query/form param  | Authorize-time hint from the relying party (per OIDC Core 1.0) | Yes (auto-cookie)    |
| 2        | `?culture=` / `?ui-culture=` query  | Same-origin links inside the STS host                          | No                   |
| 3        | `.AspNetCore.Culture` cookie        | Anything previously set by the user or by mechanism 1          | Yes                  |
| 4        | `Accept-Language` header            | Browser fallback                                               | No                   |

Mechanism 1 is the recommended path for relying parties — it follows the OpenID Connect Core 1.0 spec, requires zero changes to the STS host, and the user's choice is automatically persisted into the standard cookie so subsequent pages within the same authorize flow inherit it.

The STS host currently advertises **two** UI cultures out of the box: `vi` (default) and `en`. Adding a third is a configuration-only change (see [Adding a culture](#adding-a-culture) below).

---

## TL;DR — Quick recipes

### Mobile (Flutter / React Native / iOS / Android — any OIDC-conformant client)

Append `ui_locales` to the authorize request you already build. The user's device locale is the natural source.

```dart
// Flutter — flutter_appauth example
final result = await appAuth.authorizeAndExchangeCode(
  AuthorizationTokenRequest(
    'mobile-client',
    'com.example.app:/oauthredirect',
    issuer: 'https://id.example.com',
    scopes: ['openid', 'profile', 'offline_access'],
    additionalParameters: {
      'ui_locales': _uiLocaleTag(), // e.g. "vi", "en", "vi-VN en"
    },
  ),
);

String _uiLocaleTag() {
  final locale = WidgetsBinding.instance.platformDispatcher.locale;
  // Send specific + neutral so the STS picks the best match.
  return '${locale.toLanguageTag()} ${locale.languageCode}';
}
```

### Web app (React / Vue / Angular / vanilla — using oidc-client-ts or angular-auth-oidc-client)

Pass `ui_locales` in the extra-query-params bag your library exposes.

```ts
// oidc-client-ts
import { UserManager } from 'oidc-client-ts';

const userManager = new UserManager({
  authority: 'https://id.example.com',
  client_id: 'spa-client',
  redirect_uri: 'https://app.example.com/callback',
  scope: 'openid profile offline_access',
  extraQueryParams: { ui_locales: navigator.language || 'vi' },
});

await userManager.signinRedirect();
```

```ts
// angular-auth-oidc-client
provideAuth({
  config: {
    authority: 'https://id.example.com',
    clientId: 'spa-client',
    customParamsAuthRequest: { ui_locales: navigator.language || 'vi' },
    /* ... */
  },
});
```

### Server-rendered (Razor / Next.js / Django) — any backend acting as OIDC client

Same idea: the OIDC handler always exposes a hook for extra request parameters. ASP.NET Core's `AddOpenIdConnect`:

```csharp
.AddOpenIdConnect("oidc", options =>
{
    options.Authority = "https://id.example.com";
    options.ClientId = "server-app";
    options.Events.OnRedirectToIdentityProvider = ctx =>
    {
        var requestUiCulture = ctx.HttpContext.Features
            .Get<IRequestCultureFeature>()?.RequestCulture.UICulture.Name;
        if (!string.IsNullOrEmpty(requestUiCulture))
        {
            ctx.ProtocolMessage.SetParameter("ui_locales", requestUiCulture);
        }
        return Task.CompletedTask;
    };
});
```

---

## Reference — OIDC `ui_locales` semantics

`ui_locales` is defined in [OpenID Connect Core 1.0 § 3.1.2.1](https://openid.net/specs/openid-connect-core-1_0.html#AuthRequest) as:

> Optional. End-User's preferred languages and scripts for the user interface, represented as a space-separated list of BCP47 language tag values, ordered by preference. For instance, the value `"fr-CA fr en"` represents a preference for French as spoken in Canada, then French (without a region designation), followed by English (without a region designation). An error SHOULD NOT result if some or all of the requested locales are not supported by the OpenID Provider.

### How the STS resolves the parameter

For each tag in the list (left to right), the STS:

1. Tries an exact case-insensitive match against `RequestLocalizationOptions.SupportedUICultures` (e.g. `vi-VN` → `vi-VN`).
2. Falls back to RFC 4647 lookup — strips the trailing `-segment` and tries again. So `vi-VN` matches the host-supported `vi` after one truncation step.
3. Skips tags that fail to parse as a valid `CultureInfo`.
4. The first tag that matches wins; later tags in the list are ignored.
5. If no tag matches, the provider returns `null` and the request falls through to the query / cookie / Accept-Language providers.

### Examples

Assume `SupportedUICultures = ["vi", "en"]`:

| Input `ui_locales` value          | Resolved UI culture | Notes                                                         |
| --------------------------------- | ------------------- | ------------------------------------------------------------- |
| `vi`                              | `vi`                | Exact match.                                                  |
| `en`                              | `en`                | Exact match.                                                  |
| `vi-VN`                           | `vi`                | RFC 4647 lookup, parent match.                                |
| `VI`                              | `vi`                | Case-insensitive.                                             |
| `fr-CA fr en`                     | `en`                | First two tags do not match; `en` does.                       |
| `fr de`                           | (fall through)      | No match → next provider runs (query → cookie → Accept-Lang). |
| `not-a-real-tag vi`               | `vi`                | Malformed tag skipped, `vi` matches.                          |

### Persistence

When the STS resolves a culture from `ui_locales`, it also writes the standard ASP.NET Core culture cookie:

- Name: `.AspNetCore.Culture`
- Value: `c=<culture>|uic=<culture>` (e.g. `c=vi|uic=vi`)
- `Expires`: now + 1 year
- `Path`: `/`
- `HttpOnly`: false (the existing language switcher form needs the value to be readable from the client)
- `SameSite`: `Lax`
- `Secure`: matches `Request.IsHttps`

This persistence is automatic and idempotent. If a user lands on `/connect/authorize?ui_locales=en`, gets bounced to `/Account/Login`, then clicks the in-page language switcher to switch to `vi`, the cookie is overwritten with the new choice — `ui_locales` does not lock subsequent navigation.

The cookie is the same one that `HomeController.SetLanguage` (the in-page switcher) writes, so the two mechanisms compose.

---

## Per-platform details

### Flutter / Dart

```dart
String _uiLocalesTag() {
  // PlatformDispatcher.locale is the device's primary locale.
  final primary = WidgetsBinding.instance.platformDispatcher.locale;
  // Optional: include all configured locales so a multi-language device
  // still picks the right STS-supported culture.
  final all = WidgetsBinding.instance.platformDispatcher.locales
      .map((l) => l.toLanguageTag())
      .toList();

  // Always include the primary tag plus its language-only fallback.
  // Example output: "vi-VN vi en-US en"
  return all.join(' ');
}
```

Combine with any OIDC AppAuth-based plugin (`flutter_appauth`, `oidc_client`) by passing the value through the plugin's `additionalParameters` / `extraQueryParams` map. The parameter name is `ui_locales` (snake-case, with underscore).

### iOS (AppAuth-iOS)

```swift
let additional = ["ui_locales": Locale.preferredLanguages.joined(separator: " ")]
let request = OIDAuthorizationRequest(
    configuration: configuration,
    clientId: "ios-client",
    scopes: [OIDScopeOpenID, OIDScopeProfile, "offline_access"],
    redirectURL: redirectURL,
    responseType: OIDResponseTypeCode,
    additionalParameters: additional)
```

### Android (AppAuth-Android)

```kotlin
val request = AuthorizationRequest.Builder(
    configuration,
    "android-client",
    ResponseTypeValues.CODE,
    redirectUri)
    .setScopes("openid", "profile", "offline_access")
    .setAdditionalParameters(mapOf("ui_locales" to Locale.getDefault().toLanguageTag()))
    .build()
```

### React (oidc-client-ts)

```ts
const userManager = new UserManager({
  authority: 'https://id.example.com',
  client_id: 'spa-client',
  redirect_uri: window.location.origin + '/callback',
  scope: 'openid profile offline_access',
  // extraQueryParams flows into the /connect/authorize query string.
  extraQueryParams: { ui_locales: navigator.language },
});

await userManager.signinRedirect();
```

If your app already lets the user pick a preferred language separately from the browser's `navigator.language`, send the user's chosen tag instead. For backward compatibility, you can also send a multi-tag preference list:

```ts
extraQueryParams: { ui_locales: `${userPreferredLocale} ${navigator.language}` },
```

### Angular (angular-auth-oidc-client)

```ts
provideAuth({
  config: {
    authority: 'https://id.example.com',
    clientId: 'spa-client',
    redirectUrl: window.location.origin + '/callback',
    scope: 'openid profile offline_access',
    customParamsAuthRequest: { ui_locales: navigator.language || 'vi' },
  },
});
```

### Next.js / NextAuth.js

NextAuth's OIDC provider exposes `authorization.params`:

```ts
// pages/api/auth/[...nextauth].ts
import NextAuth from 'next-auth';
import { Provider } from 'next-auth/providers';

const sts: Provider = {
  id: 'sts',
  name: 'STS',
  type: 'oauth',
  wellKnown: 'https://id.example.com/.well-known/openid-configuration',
  authorization: { params: { scope: 'openid profile', ui_locales: 'vi' } },
  // ...
};

export default NextAuth({ providers: [sts] });
```

### Server-side OIDC handlers

ASP.NET Core (above), Spring Security, Express + `passport-openidconnect`, etc. all expose a hook to set extra parameters on the authorize URL. Search for the literal string `ui_locales` in your library's docs — it's a first-class OIDC concept.

---

## Adding a culture

Adding a third UI culture (say `ja`) requires no code change. Operators do:

1. Drop `Resources/Views/Account/Login.ja.resx`, `Resources/Views/Account/LoginWithPhone/Verify.ja.resx`, `Resources/Views/Shared/_PhoneRequestPanel.ja.resx`, `Resources/Controllers/AccountController.ja.resx`, and `Resources/Controllers/PhoneLoginController.ja.resx` into the STS host project (use the existing `vi` files as templates).
2. Update `appsettings.json`:
   ```json
   "CultureConfiguration": {
     "Cultures": [ "vi", "en", "ja" ],
     "DefaultCulture": "vi"
   }
   ```
3. Restart the STS host. The startup-time `LocalizationManifestValidator` will log Warnings under category `Localization` for any missing keys.

Once deployed, mobile/web clients can immediately start sending `ui_locales=ja` — no client-side change required beyond passing the new tag.

---

## Troubleshooting

### The login page renders in the wrong language even though I sent `ui_locales`

1. Confirm the parameter is on the authorize URL: `GET /connect/authorize?...&ui_locales=vi`. Browser dev tools → Network tab → check the request URL.
2. Confirm the tag is in `SupportedUICultures` (the resolved set is logged at startup; check the STS logs or the `appsettings.json` `CultureConfiguration:Cultures` array).
3. Check the `Set-Cookie` response header for the first authorize redirect — it should contain `.AspNetCore.Culture=c=vi|uic=vi`. If it does not, the provider did not match the tag.
4. If the user previously set a different language via the in-page switcher, that cookie wins on subsequent navigations. To override, send a fresh `ui_locales` on the next authorize request — it will rewrite the cookie.

### My SDK does not let me pass extra authorize parameters

Most modern OIDC SDKs expose this. If yours genuinely doesn't, you have two fallbacks:

- **Cookie pre-set**: if the relying party shares an apex domain with the STS (e.g. `app.example.com` and `id.example.com` both under `.example.com`), set the `.AspNetCore.Culture` cookie from the relying party with `Domain=.example.com` before redirecting to authorize. The cookie provider (priority 3) will pick it up.
- **Query string**: `?culture=vi&ui-culture=vi` on the first link the user follows into the STS host. Use this for non-OIDC entry points like the password-reset email link.

### Localization gaps

Production STS logs missing-key warnings under category `Localization`. Tail these logs after adding a new culture to catch resx gaps before users do.

---

## What changed on the STS side

This bridge was added as part of the `login-ui-redesign-i18n` feature. Implementation:

- New: `src/Skoruba.Duende.IdentityServer.STS.Identity/Helpers/Localization/OidcUiLocalesRequestCultureProvider.cs`
- Wiring: `Helpers/StartupHelpers.cs` registers `OidcUiLocalesRequestCultureProvider` as the **first** provider in `RequestLocalizationOptions.RequestCultureProviders`, and adds a tiny middleware that copies the resolved culture to the standard cookie when the OIDC provider matched.

No public OIDC contract changed; the parameter was already valid input per OIDC Core. The STS now simply respects it.
