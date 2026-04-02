using System.Net.Http;
using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.Base
{
    public class BaseClassFixture : IClassFixture<TestFixture>
    {
        protected readonly HttpClient Client;

        public BaseClassFixture(TestFixture fixture)
        {
            Client = fixture.Client;
        }
    }
}
