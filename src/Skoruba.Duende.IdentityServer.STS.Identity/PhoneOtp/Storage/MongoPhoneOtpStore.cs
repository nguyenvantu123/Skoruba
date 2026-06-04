using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Storage;

public sealed class MongoPhoneOtpStore : IPhoneOtpStore
{
    private readonly IMongoCollection<MongoOtpRecordDocument> _collection;
    private readonly string _prefix;
    private int _indexesInitialized;

    public MongoPhoneOtpStore(IOptions<PhoneOtpLoginConfiguration> options)
    {
        var config = options.Value;

        if (string.IsNullOrWhiteSpace(config.MongoConnectionString))
        {
            throw new InvalidOperationException(
                "PhoneOtpLogin:MongoConnectionString must be configured when PhoneOtpLogin:StoreProvider=MongoDb.");
        }

        if (string.IsNullOrWhiteSpace(config.MongoDatabase))
        {
            throw new InvalidOperationException(
                "PhoneOtpLogin:MongoDatabase must be configured when PhoneOtpLogin:StoreProvider=MongoDb.");
        }

        if (string.IsNullOrWhiteSpace(config.MongoCollection))
        {
            throw new InvalidOperationException(
                "PhoneOtpLogin:MongoCollection must be configured when PhoneOtpLogin:StoreProvider=MongoDb.");
        }

        var client = new MongoClient(config.MongoConnectionString);
        var database = client.GetDatabase(config.MongoDatabase);
        _collection = database.GetCollection<MongoOtpRecordDocument>(config.MongoCollection);
        _prefix = config.RedisKeyPrefix;
    }

    public async Task<OtpStoreRecord?> GetAsync(string tenantKey, string phoneE164Hash, CancellationToken ct)
    {
        await EnsureIndexesAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var filter = Builders<MongoOtpRecordDocument>.Filter.And(
            Builders<MongoOtpRecordDocument>.Filter.Eq(x => x.Id, BuildId(tenantKey, phoneE164Hash)),
            Builders<MongoOtpRecordDocument>.Filter.Gt(x => x.ExpiresAtUtc, now));

        var document = await _collection.Find(filter).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return document?.ToRecord();
    }

    public async Task SetAsync(string tenantKey, string phoneE164Hash, OtpStoreRecord record, TimeSpan ttl, CancellationToken ct)
    {
        await EnsureIndexesAsync(ct).ConfigureAwait(false);

        var document = MongoOtpRecordDocument.FromRecord(BuildId(tenantKey, phoneE164Hash), tenantKey, phoneE164Hash, record);
        var filter = Builders<MongoOtpRecordDocument>.Filter.Eq(x => x.Id, document.Id);

        await _collection.ReplaceOneAsync(
            filter,
            document,
            new ReplaceOptions { IsUpsert = true },
            ct).ConfigureAwait(false);
    }

    public async Task<int> IncrementAttemptAsync(string tenantKey, string phoneE164Hash, CancellationToken ct)
    {
        await EnsureIndexesAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var filter = Builders<MongoOtpRecordDocument>.Filter.And(
            Builders<MongoOtpRecordDocument>.Filter.Eq(x => x.Id, BuildId(tenantKey, phoneE164Hash)),
            Builders<MongoOtpRecordDocument>.Filter.Gt(x => x.ExpiresAtUtc, now));

        var update = Builders<MongoOtpRecordDocument>.Update.Inc(x => x.AttemptCount, 1);

        var updated = await _collection.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<MongoOtpRecordDocument>
            {
                ReturnDocument = ReturnDocument.After
            },
            ct).ConfigureAwait(false);

        return updated?.AttemptCount ?? 0;
    }

    public async Task DeleteAsync(string tenantKey, string phoneE164Hash, CancellationToken ct)
    {
        await EnsureIndexesAsync(ct).ConfigureAwait(false);

        var filter = Builders<MongoOtpRecordDocument>.Filter.Eq(x => x.Id, BuildId(tenantKey, phoneE164Hash));
        await _collection.DeleteOneAsync(filter, ct).ConfigureAwait(false);
    }

    private string BuildId(string tenantKey, string phoneE164Hash)
        => $"{_prefix}rec:{tenantKey}:{phoneE164Hash}";

    private async Task EnsureIndexesAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _indexesInitialized, 1) == 1)
        {
            return;
        }

        var ttlIndex = new CreateIndexModel<MongoOtpRecordDocument>(
            Builders<MongoOtpRecordDocument>.IndexKeys.Ascending(x => x.ExpiresAtUtc),
            new CreateIndexOptions
            {
                Name = "phone_otp_expires_ttl",
                ExpireAfter = TimeSpan.Zero
            });

        var lookupIndex = new CreateIndexModel<MongoOtpRecordDocument>(
            Builders<MongoOtpRecordDocument>.IndexKeys
                .Ascending(x => x.TenantKey)
                .Ascending(x => x.PhoneE164Hash),
            new CreateIndexOptions
            {
                Name = "phone_otp_lookup",
                Unique = true
            });

        await _collection.Indexes.CreateManyAsync(new[] { ttlIndex, lookupIndex }, ct).ConfigureAwait(false);
    }

    private sealed class MongoOtpRecordDocument
    {
        [BsonId]
        public string Id { get; init; } = string.Empty;

        public string TenantKey { get; init; } = string.Empty;

        public string PhoneE164Hash { get; init; } = string.Empty;

        public byte[] OtpHash { get; init; } = Array.Empty<byte>();

        public string PhoneE164 { get; init; } = string.Empty;

        public string UserId { get; init; } = string.Empty;

        public IReadOnlyList<string> CandidateUserIds { get; init; } = Array.Empty<string>();

        public DateTimeOffset CreatedAtUtc { get; init; }

        public DateTimeOffset ExpiresAtUtc { get; init; }

        public int AttemptCount { get; init; }

        public OtpStoreRecord ToRecord() => new()
        {
            OtpHash = OtpHash,
            TenantKey = TenantKey,
            PhoneE164 = PhoneE164,
            UserId = UserId,
            CandidateUserIds = CandidateUserIds,
            CreatedAtUtc = CreatedAtUtc,
            ExpiresAtUtc = ExpiresAtUtc,
            AttemptCount = AttemptCount
        };

        public static MongoOtpRecordDocument FromRecord(string id, string tenantKey, string phoneE164Hash, OtpStoreRecord record) => new()
        {
            Id = id,
            TenantKey = tenantKey,
            PhoneE164Hash = phoneE164Hash,
            OtpHash = record.OtpHash,
            PhoneE164 = record.PhoneE164,
            UserId = record.UserId,
            CandidateUserIds = record.CandidateUserIds,
            CreatedAtUtc = record.CreatedAtUtc,
            ExpiresAtUtc = record.ExpiresAtUtc,
            AttemptCount = record.AttemptCount
        };
    }
}
