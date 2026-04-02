using System.Net;
using System.Text.Json;
using FluentAssertions;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Common;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.Base;
using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests
{
    public class TenantsControllerTests : BaseClassFixture
    {
        public TenantsControllerTests(TestFixture fixture) : base(fixture)
        {
        }

        [Fact]
        public async Task GetPublicTenantsWithoutAuthenticationAndSearchReturnsOk()
        {
            Client.DefaultRequestHeaders.Clear();

            var response = await Client.GetAsync("/api/tenants/public?search=branch");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);

            document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
            document.RootElement.GetArrayLength().Should().Be(2);

            var firstTenant = document.RootElement[0];
            firstTenant.GetProperty("displayName").GetString().Should().Be("Branch A");
            firstTenant.TryGetProperty("tenantKey", out _).Should().BeFalse();
            firstTenant.TryGetProperty("redirectUrl", out _).Should().BeFalse();
            firstTenant.TryGetProperty("logoUrl", out _).Should().BeFalse();
        }

        [Fact]
        public async Task GetPublicTenantsSupportsSearchFiltering()
        {
            Client.DefaultRequestHeaders.Clear();

            var response = await Client.GetAsync("/api/tenants/public?search=branch b");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);

            document.RootElement.GetArrayLength().Should().Be(1);
            document.RootElement[0].GetProperty("displayName").GetString().Should().Be("Branch B");
        }

        [Fact]
        public async Task GetPublicTenantsWithoutSearchReturnsBadRequest()
        {
            Client.DefaultRequestHeaders.Clear();

            var response = await Client.GetAsync("/api/tenants/public");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task GetPublicTenantsWithTooShortSearchReturnsBadRequest()
        {
            Client.DefaultRequestHeaders.Clear();

            var response = await Client.GetAsync("/api/tenants/public?search=a");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task GetPublicTenantsWithTooLongSearchReturnsBadRequest()
        {
            Client.DefaultRequestHeaders.Clear();

            var search = new string('a', 101);
            var response = await Client.GetAsync($"/api/tenants/public?search={search}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task GetTenantsWithoutAuthenticationReturnsUnauthorized()
        {
            Client.DefaultRequestHeaders.Clear();

            var response = await Client.GetAsync("/api/tenants");

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }
}
