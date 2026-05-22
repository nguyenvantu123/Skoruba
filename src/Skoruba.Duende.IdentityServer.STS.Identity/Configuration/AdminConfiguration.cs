// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

namespace Skoruba.Duende.IdentityServer.STS.Identity.Configuration
{
    public class AdminConfiguration
    {
        public string PageTitle { get; set; }
        public string HomePageLogoUri { get; set; }
        public string FaviconUri { get; set; }
        public string IdentityAdminBaseUrl { get; set; }
        public string IdentityAdminClientId { get; set; }
        public string AdministrationRole { get; set; }
        public string TenantAdminRole { get; set; }
        public bool AllowSuperAdminOnTenantHost { get; set; }

        public string Theme { get; set; }

        public string CustomThemeCss { get; set; }

#nullable enable
        public string? TermsOfServiceUri { get; set; }

        public string? PrivacyPolicyUri { get; set; }

        public string? SupportUri { get; set; }

        public string? MarketingProductsUri { get; set; }

        public string? MarketingFeaturesUri { get; set; }

        public string? MarketingPricingUri { get; set; }
#nullable restore
    }
}