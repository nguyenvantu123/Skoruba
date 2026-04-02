using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TenantWebApp.Identity;

public sealed class TenantAdminAccountApiOptions
{
    public const string SectionName = "TenantAdminAccountApi";
    public string StsBaseUrl { get; set; } = string.Empty;
}

public sealed class TenantAdminAccountDto
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public bool HasPassword { get; set; }
    public string AccountType { get; set; } = string.Empty;
    public string AuthSource { get; set; } = string.Empty;
    public string ExternalIdentityId { get; set; } = string.Empty;
}

public sealed class TenantAdminPersonalDataDto
{
    public string UserId { get; set; } = string.Empty;
    public string TenantKey { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool EmailConfirmed { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string Profile { get; set; } = string.Empty;
    public string StreetAddress { get; set; } = string.Empty;
    public string Locality { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public Dictionary<string, string> PersonalData { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TenantAdminChangePasswordDto
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public sealed class TenantAdminChangePasswordResultDto
{
    public bool Success { get; set; }
    public List<string> Errors { get; set; } = new();
}

public sealed class TenantAdminTwoFactorStatusDto
{
    public bool HasAuthenticator { get; set; }
    public bool IsTwoFactorEnabled { get; set; }
    public int RecoveryCodesLeft { get; set; }
}

public sealed class TenantAdminTwoFactorSetupDto
{
    public string SharedKey { get; set; } = string.Empty;
    public string AuthenticatorUri { get; set; } = string.Empty;
}

public sealed class TenantAdminVerifyTwoFactorDto
{
    public string Code { get; set; } = string.Empty;
}

public sealed class TenantAdminTwoFactorOperationResultDto
{
    public bool Success { get; set; }
    public TenantAdminTwoFactorStatusDto? Status { get; set; }
    public TenantAdminTwoFactorSetupDto? Setup { get; set; }
    public List<string> RecoveryCodes { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public interface ITenantAdminAccountClient
{
    Task<TenantAdminAccountDto?> GetMeAsync(CancellationToken cancellationToken = default);
    Task<TenantAdminPersonalDataDto?> GetPersonalDataAsync(CancellationToken cancellationToken = default);
    Task<TenantAdminChangePasswordResultDto?> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);
    Task<TenantAdminTwoFactorStatusDto?> GetTwoFactorStatusAsync(CancellationToken cancellationToken = default);
    Task<TenantAdminTwoFactorSetupDto?> GetTwoFactorSetupAsync(CancellationToken cancellationToken = default);
    Task<TenantAdminTwoFactorOperationResultDto?> EnableTwoFactorAsync(string code, CancellationToken cancellationToken = default);
    Task<TenantAdminTwoFactorOperationResultDto?> DisableTwoFactorAsync(CancellationToken cancellationToken = default);
    Task<TenantAdminTwoFactorOperationResultDto?> ResetAuthenticatorAsync(CancellationToken cancellationToken = default);
    Task<TenantAdminTwoFactorOperationResultDto?> GenerateRecoveryCodesAsync(CancellationToken cancellationToken = default);
}

public sealed class TenantAdminAccountClient : ITenantAdminAccountClient
{
    private readonly HttpClient _httpClient;

    public TenantAdminAccountClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<TenantAdminAccountDto?> GetMeAsync(CancellationToken cancellationToken = default)
        => _httpClient.GetFromJsonAsync<TenantAdminAccountDto>("api/tenant-admin/account/me", cancellationToken);

    public Task<TenantAdminPersonalDataDto?> GetPersonalDataAsync(CancellationToken cancellationToken = default)
        => _httpClient.GetFromJsonAsync<TenantAdminPersonalDataDto>("api/tenant-admin/account/personal-data", cancellationToken);

    public async Task<TenantAdminChangePasswordResultDto?> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PutAsJsonAsync("api/tenant-admin/account/password", new TenantAdminChangePasswordDto
        {
            CurrentPassword = currentPassword,
            NewPassword = newPassword
        }, cancellationToken);

        return await response.Content.ReadFromJsonAsync<TenantAdminChangePasswordResultDto>(cancellationToken: cancellationToken);
    }

    public Task<TenantAdminTwoFactorStatusDto?> GetTwoFactorStatusAsync(CancellationToken cancellationToken = default)
        => _httpClient.GetFromJsonAsync<TenantAdminTwoFactorStatusDto>("api/tenant-admin/account/two-factor", cancellationToken);

    public async Task<TenantAdminTwoFactorSetupDto?> GetTwoFactorSetupAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync("api/tenant-admin/account/two-factor/setup", content: null, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TenantAdminTwoFactorSetupDto>(cancellationToken: cancellationToken);
    }

    public async Task<TenantAdminTwoFactorOperationResultDto?> EnableTwoFactorAsync(string code, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/tenant-admin/account/two-factor/enable", new TenantAdminVerifyTwoFactorDto { Code = code }, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TenantAdminTwoFactorOperationResultDto>(cancellationToken: cancellationToken);
    }

    public async Task<TenantAdminTwoFactorOperationResultDto?> DisableTwoFactorAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync("api/tenant-admin/account/two-factor/disable", content: null, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TenantAdminTwoFactorOperationResultDto>(cancellationToken: cancellationToken);
    }

    public async Task<TenantAdminTwoFactorOperationResultDto?> ResetAuthenticatorAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync("api/tenant-admin/account/two-factor/reset-authenticator", content: null, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TenantAdminTwoFactorOperationResultDto>(cancellationToken: cancellationToken);
    }

    public async Task<TenantAdminTwoFactorOperationResultDto?> GenerateRecoveryCodesAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync("api/tenant-admin/account/two-factor/recovery-codes", content: null, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TenantAdminTwoFactorOperationResultDto>(cancellationToken: cancellationToken);
    }
}

public static class TenantAdminAccountClientServiceCollectionExtensions
{
    public static IServiceCollection AddTenantAdminAccountClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TenantAdminAccountApiOptions>().Bind(configuration.GetSection(TenantAdminAccountApiOptions.SectionName));

        services.AddHttpClient<ITenantAdminAccountClient, TenantAdminAccountClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<TenantAdminAccountApiOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.StsBaseUrl))
            {
                throw new InvalidOperationException($"Missing config: {TenantAdminAccountApiOptions.SectionName}:StsBaseUrl");
            }

            client.BaseAddress = new Uri(options.StsBaseUrl, UriKind.Absolute);
        });

        return services;
    }
}
