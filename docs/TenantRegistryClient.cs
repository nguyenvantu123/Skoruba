using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace BlazorApiUser.Tenants;

public sealed class TenantRegistryOptions
{
    public const string SectionName = "TenantRegistry";

    public string StsBaseUrl { get; set; } = string.Empty;
}

public sealed class TenantRegistryLookupResult
{
    public string TenantId { get; set; } = string.Empty;

    public string Identifier { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string SecretName { get; set; } = string.Empty;

    public Dictionary<string, string> ConnectionSecrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsActive { get; set; }
}

public interface ITenantRegistryClient
{
    Task<TenantRegistryLookupResult?> GetCurrentTenantAsync(string serviceName, CancellationToken cancellationToken = default);
}

public sealed class TenantRegistryClient : ITenantRegistryClient
{
    private readonly HttpClient _httpClient;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TenantRegistryClient(HttpClient httpClient, IHttpContextAccessor httpContextAccessor)
    {
        _httpClient = httpClient;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<TenantRegistryLookupResult?> GetCurrentTenantAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var authorization = _httpContextAccessor.HttpContext?.Request.Headers[HeaderNames.Authorization].ToString();
        if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Missing tenant admin bearer token in the current request.");
        }

        var accessToken = authorization["Bearer ".Length..].Trim();
        var relativeUrl = $"api/tenant-admin/tenant-registry?serviceName={Uri.EscapeDataString(serviceName)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TenantRegistryLookupResult>(cancellationToken: cancellationToken);
    }
}

public static class TenantRegistryClientServiceCollectionExtensions
{
    public static IServiceCollection AddTenantRegistryClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();

        services
            .AddOptions<TenantRegistryOptions>()
            .Bind(configuration.GetSection(TenantRegistryOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddHttpClient<ITenantRegistryClient, TenantRegistryClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<TenantRegistryOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.StsBaseUrl))
            {
                throw new InvalidOperationException($"Missing config: {TenantRegistryOptions.SectionName}:StsBaseUrl");
            }

            client.BaseAddress = new Uri(options.StsBaseUrl, UriKind.Absolute);
        });

        return services;
    }
}
