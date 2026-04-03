// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.



using Duende.IdentityServer.EntityFramework.Storage;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skoruba.AuditLogging.EntityFramework.DbContexts;
using Skoruba.AuditLogging.EntityFramework.Entities;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Admin.Storage.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.Configuration;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Interfaces;
using System;
using System.Linq;
using System.Reflection;

namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration.MySql
{
    public static class DatabaseExtensions
    {
        /// <summary>
        /// Register DbContexts for IdentityServer ConfigurationStore and PersistedGrants, Identity and Logging
        /// Configure the connection strings in AppSettings.json
        /// </summary>
        /// <typeparam name="TConfigurationDbContext"></typeparam>
        /// <typeparam name="TPersistedGrantDbContext"></typeparam>
        /// <typeparam name="TLogDbContext"></typeparam>
        /// <typeparam name="TIdentityDbContext"></typeparam>
        /// <typeparam name="TAuditLoggingDbContext"></typeparam>
        /// <typeparam name="TDataProtectionDbContext"></typeparam>
        /// <typeparam name="TAuditLog"></typeparam>
        /// <param name="services"></param>
        /// <param name="connectionStrings"></param>
        /// <param name="databaseMigrations"></param>
        public static void RegisterMySqlDbContexts<TIdentityDbContext, TConfigurationDbContext, TPersistedGrantDbContext, TLogDbContext, TAuditLoggingDbContext, TDataProtectionDbContext, TAdminConfigurationDbContext, TAuditLog>(this IServiceCollection services,
            ConnectionStringsConfiguration connectionStrings,
            DatabaseMigrationsConfiguration databaseMigrations)
            where TIdentityDbContext : DbContext
            where TPersistedGrantDbContext : DbContext, IAdminPersistedGrantDbContext
            where TConfigurationDbContext : DbContext, IAdminConfigurationDbContext
            where TLogDbContext : DbContext, IAdminLogDbContext
            where TAuditLoggingDbContext : DbContext, IAuditLoggingDbContext<TAuditLog>
            where TDataProtectionDbContext : DbContext, IDataProtectionKeyContext
            where TAdminConfigurationDbContext : DbContext, IAdminConfigurationStoreDbContext
            where TAuditLog : AuditLog
        {
            var migrationsAssembly = "Skoruba.Duende.IdentityServer.Admin.EntityFramework.MySql";
            var identityConnectionString = NormalizeMySqlConnectionStringForDevelopment(connectionStrings.IdentityDbConnection);
            var configurationConnectionString = NormalizeMySqlConnectionStringForDevelopment(connectionStrings.ConfigurationDbConnection);
            var persistedGrantConnectionString = NormalizeMySqlConnectionStringForDevelopment(connectionStrings.PersistedGrantDbConnection);
            var adminLogConnectionString = NormalizeMySqlConnectionStringForDevelopment(connectionStrings.AdminLogDbConnection);
            var adminAuditLogConnectionString = NormalizeMySqlConnectionStringForDevelopment(connectionStrings.AdminAuditLogDbConnection);
            var dataProtectionConnectionString = NormalizeMySqlConnectionStringForDevelopment(connectionStrings.DataProtectionDbConnection);
            var adminConfigurationConnectionString = NormalizeMySqlConnectionStringForDevelopment(connectionStrings.AdminConfigurationDbConnection);

            // Config DB for identity
            services.AddDbContext<TIdentityDbContext>(options =>
             options.UseMySQL(identityConnectionString, b => b.MigrationsAssembly(migrationsAssembly)));

            // Config DB from existing connection

            services.AddConfigurationDbContext<TConfigurationDbContext>(options =>
       options.ConfigureDbContext = b =>
           b.UseMySQL(configurationConnectionString, b => b.MigrationsAssembly(migrationsAssembly)));

            // Operational DB from existing connection
            services.AddOperationalDbContext<TPersistedGrantDbContext>(options => options.ConfigureDbContext = b => b.UseMySQL(persistedGrantConnectionString, b => b.MigrationsAssembly(migrationsAssembly)));

            // Log DB from existing connection
            services.AddDbContext<TLogDbContext>(options => options.UseMySQL(adminLogConnectionString, b => b.MigrationsAssembly(migrationsAssembly)));

            // Audit logging connection
            services.AddDbContext<TAuditLoggingDbContext>(options => options.UseMySQL(adminAuditLogConnectionString, b => b.MigrationsAssembly(migrationsAssembly)));

            // DataProtectionKey DB from existing connection
            if (!string.IsNullOrEmpty(dataProtectionConnectionString))
                services.AddDbContext<TDataProtectionDbContext>(options => options.UseMySQL(dataProtectionConnectionString, b => b.MigrationsAssembly(migrationsAssembly)));

            // Admin configuration DB from existing connection
            services.AddDbContext<TAdminConfigurationDbContext>(options => options.UseMySQL(adminConfigurationConnectionString, b => b.MigrationsAssembly(migrationsAssembly)));
        }

        /// <summary>
        /// Register DbContexts for IdentityServer ConfigurationStore and PersistedGrants and Identity
        /// Configure the connection strings in AppSettings.json
        /// </summary>
        /// <typeparam name="TConfigurationDbContext"></typeparam>
        /// <typeparam name="TPersistedGrantDbContext"></typeparam>
        /// <typeparam name="TIdentityDbContext"></typeparam>
        /// <typeparam name="TDataProtectionDbContext"></typeparam>
        /// <param name="services"></param>
        /// <param name="identityConnectionString"></param>
        /// <param name="configurationConnectionString"></param>
        /// <param name="persistedGrantConnectionString"></param>
        /// <param name="dataProtectionConnectionString"></param>
        public static void RegisterMySqlDbContexts<TIdentityDbContext, TConfigurationDbContext,
    TPersistedGrantDbContext, TDataProtectionDbContext>(this IServiceCollection services,
         string identityConnectionString, string configurationConnectionString,
            string persistedGrantConnectionString, string dataProtectionConnectionString)
    where TIdentityDbContext : DbContext
    where TPersistedGrantDbContext : DbContext, IAdminPersistedGrantDbContext
    where TConfigurationDbContext : DbContext, IAdminConfigurationDbContext
    where TDataProtectionDbContext : DbContext, IDataProtectionKeyContext
        {
            var migrationsAssembly = typeof(DatabaseExtensions).GetTypeInfo().Assembly.GetName().Name;
            identityConnectionString = NormalizeMySqlConnectionStringForDevelopment(identityConnectionString);
            configurationConnectionString = NormalizeMySqlConnectionStringForDevelopment(configurationConnectionString);
            persistedGrantConnectionString = NormalizeMySqlConnectionStringForDevelopment(persistedGrantConnectionString);
            dataProtectionConnectionString = NormalizeMySqlConnectionStringForDevelopment(dataProtectionConnectionString);

            // Identity DB
            services.AddDbContext<TIdentityDbContext>(options =>
                options.UseMySQL(identityConnectionString, mySql => mySql.MigrationsAssembly(migrationsAssembly)));

            // Configuration DB
            services.AddConfigurationDbContext<TConfigurationDbContext>(options =>
                options.ConfigureDbContext = db =>
                    db.UseMySQL(configurationConnectionString, mySql => mySql.MigrationsAssembly(migrationsAssembly)));

            // Persisted Grants DB
            services.AddOperationalDbContext<TPersistedGrantDbContext>(options =>
                options.ConfigureDbContext = db =>
                    db.UseMySQL(persistedGrantConnectionString, mySql => mySql.MigrationsAssembly(migrationsAssembly)));

            // DataProtection DB
            services.AddDbContext<TDataProtectionDbContext>(options =>
                options.UseMySQL(dataProtectionConnectionString, mySql => mySql.MigrationsAssembly(migrationsAssembly)));
        }

        /// <summary>
        /// Add Data Protection DbContext for SQL Server
        /// </summary>
        /// <param name="services"></param>
        /// <param name="connectionString"></param>
        /// <param name="migrationsAssembly"></param>
        /// <typeparam name="TDataProtectionDbContext"></typeparam>
        public static void AddDataProtectionDbContextMySql<TDataProtectionDbContext>(
            this IServiceCollection services,
            string connectionString,
            string migrationsAssembly = null)
            where TDataProtectionDbContext : DbContext, IDataProtectionKeyContext
        {
            var assembly = migrationsAssembly ?? typeof(DatabaseExtensions).GetTypeInfo().Assembly.GetName().Name;
            services.AddDbContext<TDataProtectionDbContext>(options =>
                options.UseMySQL(NormalizeMySqlConnectionStringForDevelopment(connectionString), b => b.MigrationsAssembly(migrationsAssembly)));
        }

        private static string NormalizeMySqlConnectionStringForDevelopment(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return connectionString;
            }

            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
            if (!isDevelopment)
            {
                return connectionString;
            }

            var parts = connectionString
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(part =>
                {
                    var trimmedPart = part.TrimStart();
                    return !trimmedPart.StartsWith("SslMode=", StringComparison.OrdinalIgnoreCase) &&
                           !trimmedPart.StartsWith("Ssl Mode=", StringComparison.OrdinalIgnoreCase) &&
                           !trimmedPart.StartsWith("AllowPublicKeyRetrieval=", StringComparison.OrdinalIgnoreCase);
                });

            return $"{string.Join(";", parts)};AllowPublicKeyRetrieval=True;SslMode=Disabled";
        }
    }
}
