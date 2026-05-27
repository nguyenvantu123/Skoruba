// Feature: tenant-client-cache-expansion
// JSON serializer for ClientCacheSnapshotEnvelope. Tightly scoped — kept as
// an internal static class so the option-set is the single source of truth
// for both write and read paths.
//
// Validates: Requirements 2.4, 2.7, 2.8, 10.4, 17.5
//
// Contract:
//   - System.Text.Json (no third-party serializer).
//   - camelCase property names.
//   - WriteIndented = false (R2.7).
//   - DefaultIgnoreCondition = Never => empty collections serialize as `[]`,
//     null strings serialize as `null` (R2.4).
//   - Enums via JsonStringEnumConverter(camelCase) — defensive in case the
//     DTO is ever extended with an enum field.
//   - TryDeserialize returns (null, "corrupt") on JsonException; never throws.

#nullable enable

using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

internal static class ClientCacheSnapshotSerializer
{
    /// <summary>
    /// Shared <see cref="JsonSerializerOptions"/> instance. Mutating these
    /// options after first use is unsupported by System.Text.Json — keep
    /// this read-only.
    /// </summary>
    public static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    /// <summary>
    /// Serialize <paramref name="envelope"/> to a UTF-8 byte array suitable
    /// for direct write into <c>IDistributedCache</c>.
    /// </summary>
    public static byte[] Serialize(ClientCacheSnapshotEnvelope envelope)
    {
        if (envelope is null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }

        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
    }

    /// <summary>
    /// Try to deserialize a previously written payload.
    /// </summary>
    /// <param name="payload">Raw bytes read from <c>IDistributedCache</c>.</param>
    /// <param name="failureReason">
    /// <c>null</c> on success; <c>"corrupt"</c> when the payload could not
    /// be parsed as JSON or was missing required structural fields.
    /// </param>
    /// <returns>
    /// The parsed envelope, or <c>null</c> when the payload is unusable.
    /// Never throws — Redis-side data corruption is treated as Cache_Outcome.Miss
    /// per R10.4.
    /// </returns>
    public static ClientCacheSnapshotEnvelope? TryDeserialize(byte[]? payload, out string? failureReason)
    {
        if (payload is null || payload.Length == 0)
        {
            failureReason = "corrupt";
            return null;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<ClientCacheSnapshotEnvelope>(payload, Options);
            if (envelope is null || envelope.Data is null)
            {
                failureReason = "corrupt";
                return null;
            }

            failureReason = null;
            return envelope;
        }
        catch (JsonException)
        {
            failureReason = "corrupt";
            return null;
        }
    }
}
