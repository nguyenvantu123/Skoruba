using System.Collections.Generic;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Configuration
{
    public sealed class DevelopmentOidcClientSyncConfiguration
    {
        public const string SectionName = "DevelopmentOidcClientSync";

        public bool Enabled { get; set; } = true;

        public List<ClientSyncTarget> Clients { get; set; } = new();

        public sealed class ClientSyncTarget
        {
            public string ClientId { get; set; } = string.Empty;

            public List<string> BaseUrls { get; set; } = new();
        }
    }
}
