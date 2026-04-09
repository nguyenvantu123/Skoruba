// Copyright (c) Brock Allen & Dominick Baier. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
// File: https://github.com/IdentityServer/IdentityServer4/blob/main/samples/Quickstarts/3_AspNetCoreAndApis/src/IdentityServer/Quickstart/Account/ExternalController.cs

// Modified by Jan Škoruba and J. Arturo

using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Duende.IdentityServer.Services;
using IdentityModel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Helpers
{
    public class ApplicationSignInManager<TUser> : SignInManager<TUser>
        where TUser : class
    {
        private readonly IHttpContextAccessor _contextAccessor;
        private readonly IUserSession _userSession;

        public ApplicationSignInManager(UserManager<TUser> userManager,
            IHttpContextAccessor contextAccessor,
            IUserSession userSession,
            IUserClaimsPrincipalFactory<TUser> claimsFactory,
            IOptions<IdentityOptions> optionsAccessor,
            ILogger<SignInManager<TUser>> logger,
            IAuthenticationSchemeProvider schemes,
            IUserConfirmation<TUser> confirmation) : base(userManager, contextAccessor,
                claimsFactory, optionsAccessor, logger, schemes, confirmation)
        {
            _contextAccessor = contextAccessor;
            _userSession = userSession;
        }

        public override async Task SignInWithClaimsAsync(TUser user, AuthenticationProperties authenticationProperties, IEnumerable<Claim> additionalClaims)
        {
            var claims = additionalClaims.ToList();

            var externalResult = await _contextAccessor.HttpContext.AuthenticateAsync(IdentityConstants.ExternalScheme);
            if (externalResult != null && externalResult.Succeeded)
            {
                var sid = externalResult.Principal.Claims.FirstOrDefault(x => x.Type == JwtClaimTypes.SessionId);
                if (sid != null)
                {
                    claims.Add(new Claim(JwtClaimTypes.SessionId, sid.Value));
                }

                if (authenticationProperties != null)
                {
                    // if the external provider issued an id_token, we'll keep it for sign out
                    var idToken = externalResult.Properties.GetTokenValue("id_token");
                    if (idToken != null)
                    {
                        authenticationProperties.StoreTokens(new[] { new AuthenticationToken { Name = "id_token", Value = idToken } });
                    }
                }

                var authenticationMethod = claims.FirstOrDefault(x => x.Type == ClaimTypes.AuthenticationMethod);
                var idp = claims.FirstOrDefault(x => x.Type == JwtClaimTypes.IdentityProvider);

                if (authenticationMethod != null && idp == null)
                {
                    claims.Add(new Claim(JwtClaimTypes.IdentityProvider, authenticationMethod.Value));
                }
            }

            await base.SignInWithClaimsAsync(user, authenticationProperties, claims);
            await SynchronizeIdentityServerSessionAsync();
        }

        public override async Task SignInAsync(TUser user, AuthenticationProperties authenticationProperties, string? authenticationMethod = null)
        {
            await base.SignInAsync(user, authenticationProperties, authenticationMethod);
            await SynchronizeIdentityServerSessionAsync();
        }

        public override async Task SignInAsync(TUser user, bool isPersistent, string? authenticationMethod = null)
        {
            await base.SignInAsync(user, isPersistent, authenticationMethod);
            await SynchronizeIdentityServerSessionAsync();
        }

        public override async Task RefreshSignInAsync(TUser user)
        {
            await base.RefreshSignInAsync(user);

            try
            {
                await _userSession.EnsureSessionIdCookieAsync();
                Logger.LogInformation(
                    "Ensured IdentityServer session cookie after refresh sign-in for user type {UserType}.",
                    typeof(TUser).Name);
            }
            catch (System.OperationCanceledException) when (_contextAccessor.HttpContext?.RequestAborted.IsCancellationRequested == true)
            {
                Logger.LogInformation(
                    "Skipping IdentityServer session synchronization during refresh sign-in because the request was canceled for user type {UserType}.",
                    typeof(TUser).Name);
            }
        }

        public override async Task<SignInResult> PasswordSignInAsync(string userName, string password, bool isPersistent, bool lockoutOnFailure)
        {
            var result = await base.PasswordSignInAsync(userName, password, isPersistent, lockoutOnFailure);
            await SynchronizeIdentityServerSessionAfterSuccessfulSignInAsync(result, $"username:{userName}");
            return result;
        }

        public override async Task<SignInResult> PasswordSignInAsync(TUser user, string password, bool isPersistent, bool lockoutOnFailure)
        {
            var result = await base.PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);
            await SynchronizeIdentityServerSessionAfterSuccessfulSignInAsync(result, $"user-type:{typeof(TUser).Name}");
            return result;
        }

        public override async Task<SignInResult> ExternalLoginSignInAsync(string loginProvider, string providerKey, bool isPersistent, bool bypassTwoFactor)
        {
            var result = await base.ExternalLoginSignInAsync(loginProvider, providerKey, isPersistent, bypassTwoFactor);
            await SynchronizeIdentityServerSessionAfterSuccessfulSignInAsync(result, $"external:{loginProvider}");
            return result;
        }

        public override async Task<SignInResult> TwoFactorAuthenticatorSignInAsync(string code, bool isPersistent, bool rememberClient)
        {
            var result = await base.TwoFactorAuthenticatorSignInAsync(code, isPersistent, rememberClient);
            await SynchronizeIdentityServerSessionAfterSuccessfulSignInAsync(result, "2fa:authenticator");
            return result;
        }

        public override async Task<SignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode)
        {
            var result = await base.TwoFactorRecoveryCodeSignInAsync(recoveryCode);
            await SynchronizeIdentityServerSessionAfterSuccessfulSignInAsync(result, "2fa:recovery");
            return result;
        }

        private async Task SynchronizeIdentityServerSessionAsync()
        {
            try
            {
                var httpContext = _contextAccessor.HttpContext;
                if (httpContext == null)
                {
                    return;
                }

                var applicationResult = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
                if (applicationResult?.Succeeded != true || applicationResult.Principal == null || applicationResult.Properties == null)
                {
                    Logger.LogWarning(
                        "Skipping IdentityServer session synchronization because the application authentication cookie could not be reloaded after sign-in for user type {UserType}.",
                        typeof(TUser).Name);
                    return;
                }

                await _userSession.CreateSessionIdAsync(applicationResult.Principal, applicationResult.Properties);
                Logger.LogInformation(
                    "Synchronized IdentityServer session cookie after application sign-in for user type {UserType}. HasSessionIdClaim={HasSessionIdClaim}",
                    typeof(TUser).Name,
                    applicationResult.Principal.HasClaim(x => x.Type == JwtClaimTypes.SessionId));
            }
            catch (System.OperationCanceledException) when (_contextAccessor.HttpContext?.RequestAborted.IsCancellationRequested == true)
            {
                Logger.LogInformation(
                    "Skipping IdentityServer session synchronization because the request was canceled for user type {UserType}.",
                    typeof(TUser).Name);
            }
        }

        private async Task SynchronizeIdentityServerSessionAfterSuccessfulSignInAsync(SignInResult result, string source)
        {
            if (!result.Succeeded)
            {
                Logger.LogInformation(
                    "Skipping IdentityServer session synchronization after sign-in source {Source} because sign-in did not succeed. RequiresTwoFactor={RequiresTwoFactor}, IsLockedOut={IsLockedOut}, IsNotAllowed={IsNotAllowed}",
                    source,
                    result.RequiresTwoFactor,
                    result.IsLockedOut,
                    result.IsNotAllowed);
                return;
            }

            Logger.LogInformation(
                "Sign-in source {Source} succeeded. Synchronizing IdentityServer session cookie.",
                source);

            await SynchronizeIdentityServerSessionAsync();
        }
    }
}

