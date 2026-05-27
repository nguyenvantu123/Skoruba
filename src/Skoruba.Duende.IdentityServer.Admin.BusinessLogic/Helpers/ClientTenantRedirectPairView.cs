// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

namespace Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Helpers
{
    /// <summary>
    /// Public, read-only projection of the legacy
    /// <c>skoruba_tenant_redirect_pairs</c> client-property entry, exposed
    /// from <see cref="ClientTenantRedirectPairsHelper.TryParsePairs"/>.
    ///
    /// Existing BusinessLogic callers continue to use the richer
    /// <c>ClientTenantRedirectPairDto</c> (which carries CORS origin and
    /// internal hand-off fields). This view-only record is what the
    /// <c>Admin.UI.Api</c> tenant scope resolver needs and nothing more —
    /// keeping the helper's public surface minimal.
    /// </summary>
    public sealed record ClientTenantRedirectPairView(
        string TenantKey,
        string SignInRedirectUri,
        string SignOutRedirectUri);
}
