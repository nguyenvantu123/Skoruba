// Copyright (c) Duende Software. All rights reserved.
// See LICENSE in the project root for license information.

// Original file: https://github.com/DuendeSoftware/IdentityServer.Quickstart.UI
// Modified by Jan Škoruba

using Duende.IdentityServer;
using Duende.IdentityServer.Events;
using Duende.IdentityServer.Extensions;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Stores;
using IdentityModel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity;
using Skoruba.Duende.IdentityServer.Shared.Configuration.Configuration.Identity;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.Helpers;
using Skoruba.Duende.IdentityServer.STS.Identity.Helpers.Localization;
using Skoruba.Duende.IdentityServer.STS.Identity.ViewModels.Account;
using System;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using TenantInfrastructure.Abstractions;
using TenantInfrastructure.Identity;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Controllers
{
    [SecurityHeaders]
    [Authorize]
    public class AccountController<TUser, TKey> : Controller
        where TUser : IdentityUser<TKey>, new()
        where TKey : IEquatable<TKey>
    {
        private readonly UserResolver<TUser> _userResolver;
        private readonly UserManager<TUser> _userManager;
        private readonly ApplicationSignInManager<TUser> _signInManager;
        private readonly IIdentityServerInteractionService _interaction;
        private readonly IClientStore _clientStore;
        private readonly IAuthenticationSchemeProvider _schemeProvider;
        private readonly IEventService _events;
        private readonly IEmailSender _emailSender;
        private readonly IGenericControllerLocalizer<AccountController<TUser, TKey>> _localizer;
        private readonly LoginConfiguration _loginConfiguration;
        private readonly RegisterConfiguration _registerConfiguration;
        private readonly IdentityOptions _identityOptions;
        private readonly ILogger<AccountController<TUser, TKey>> _logger;
        private readonly IIdentityProviderStore _identityProviderStore;
        private readonly ITenantContextAccessor _tenantAccessor;
        private readonly ITenantUserValidator _tenantUserValidator;
        private readonly Skoruba.Duende.IdentityServer.STS.Identity.Configuration.AdminConfiguration _adminConfiguration;
        private readonly ITenantStore _tenantStore;

        public AccountController(
            UserResolver<TUser> userResolver,
            UserManager<TUser> userManager,
            ApplicationSignInManager<TUser> signInManager,
            IIdentityServerInteractionService interaction,
            IClientStore clientStore,
            IAuthenticationSchemeProvider schemeProvider,
            IEventService events,
            IEmailSender emailSender,
            IGenericControllerLocalizer<AccountController<TUser, TKey>> localizer,
            LoginConfiguration loginConfiguration,
            RegisterConfiguration registerConfiguration,
            IdentityOptions identityOptions,
            ILogger<AccountController<TUser, TKey>> logger,
            IIdentityProviderStore identityProviderStore,
            ITenantContextAccessor tenantAccessor,
            ITenantUserValidator tenantUserValidator,
            Skoruba.Duende.IdentityServer.STS.Identity.Configuration.AdminConfiguration adminConfiguration,
            ITenantStore tenantStore)
        {
            _userResolver = userResolver;
            _userManager = userManager;
            _signInManager = signInManager;
            _interaction = interaction;
            _clientStore = clientStore;
            _schemeProvider = schemeProvider;
            _events = events;
            _emailSender = emailSender;
            _localizer = localizer;
            _loginConfiguration = loginConfiguration;
            _registerConfiguration = registerConfiguration;
            _identityOptions = identityOptions;
            _logger = logger;
            _identityProviderStore = identityProviderStore;
            _tenantAccessor = tenantAccessor;
            _tenantUserValidator = tenantUserValidator;
            _adminConfiguration = adminConfiguration;
            _tenantStore = tenantStore;
        }

        /// <summary>
        /// Entry point into the login workflow
        /// </summary>
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> Login(string returnUrl)
        {
            // build a model so we know what to show on the login page
            var vm = await BuildLoginViewModelAsync(returnUrl);

            if (vm.EnableLocalLogin == false && vm.ExternalProviders.Count() == 1)
            {
                // only one option for logging in
                return ExternalLogin(vm.ExternalProviders.First().AuthenticationScheme, returnUrl);
            }

            return View(vm);
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<ActionResult<LoginTenantStatusViewModel>> TenantStatus()
        {
            var tenantKey = _tenantAccessor.Current?.TenantKey;
            if (string.IsNullOrWhiteSpace(tenantKey))
            {
                return Ok(new LoginTenantStatusViewModel());
            }

            try
            {
                var tenant = await _tenantStore.FindAsync(tenantKey, HttpContext.RequestAborted);
                if (tenant == null)
                {
                    return Ok(new LoginTenantStatusViewModel
                    {
                        State = "missing",
                        TenantKey = tenantKey,
                        Message = "We could not load your tenant details. You can still sign in."
                    });
                }

                return Ok(new LoginTenantStatusViewModel
                {
                    State = "resolved",
                    TenantKey = tenant.TenantKey,
                    DisplayName = string.IsNullOrWhiteSpace(tenant.DisplayName) ? tenant.TenantKey : tenant.DisplayName,
                    LogoUrl = tenant.LogoUrl
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to load tenant details for login page.");

                return Ok(new LoginTenantStatusViewModel
                {
                    State = "error",
                    TenantKey = tenantKey,
                    Message = "We could not confirm your tenant details right now. You can still sign in."
                });
            }
        }

        /// <summary>
        /// Handle postback from username/password login
        /// </summary>
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginInputModel model, string button)
        {
            // check if we are in the context of an authorization request
            var context = await _interaction.GetAuthorizationContextAsync(model.ReturnUrl);

            // the user clicked the "cancel" button
            if (button != "login")
            {
                if (context != null)
                {
                    // if the user cancels, send a result back into IdentityServer as if they 
                    // denied the consent (even if this client does not require consent).
                    // this will send back an access denied OIDC error response to the client.
                    await _interaction.DenyAuthorizationAsync(context, AuthorizationError.AccessDenied);

                    // we can trust model.ReturnUrl since GetAuthorizationContextAsync returned non-null
                    if (context.IsNativeClient())
                    {
                        // The client is native, so this change in how to
                        // return the response is for better UX for the end user.
                        return this.LoadingPage("Redirect", model.ReturnUrl);
                    }

                    return Redirect(model.ReturnUrl);
                }

                // since we don't have a valid context, then we just go back to the home page
                return Redirect("~/");
            }

            if (ModelState.IsValid)
            {
                var user = await _userResolver.GetUserAsync(model.Username);
                if (user != default(TUser))
                {
                    try
                    {
                        await EnsureLoginAllowedAsync(user);
                        await EnsureClientAllowedAsync(user, context);
                    }
                    catch (SecurityException ex)
                    {
                        var redirectResult = await TryTenantAdminRedirectViewAsync(user, context);
                        if (redirectResult != null)
                        {
                            return redirectResult;
                        }

                        ModelState.AddModelError(string.Empty, ex.Message);
                        var forbiddenVm = await BuildLoginViewModelAsync(model);
                        return View(forbiddenVm);
                    }

                    var result = await _signInManager.PasswordSignInAsync(user.UserName, model.Password, model.RememberLogin, lockoutOnFailure: true);
                    if (result.Succeeded)
                    {
                        await _events.RaiseAsync(new UserLoginSuccessEvent(user.UserName, user.Id.ToString(), user.UserName));

                        if (context != null)
                        {
                            if (context.IsNativeClient())
                            {
                                // The client is native, so this change in how to
                                // return the response is for better UX for the end user.
                                return this.LoadingPage("Redirect", model.ReturnUrl);
                            }

                            // we can trust model.ReturnUrl since GetAuthorizationContextAsync returned non-null
                            return Redirect(model.ReturnUrl);
                        }
                        return await RedirectToLocalAsync(model.ReturnUrl);
                    }

                    if (result.RequiresTwoFactor)
                    {
                        return RedirectToAction(nameof(LoginWith2fa), new { model.ReturnUrl, RememberMe = model.RememberLogin });
                    }

                    if (result.IsLockedOut)
                    {
                        return View("Lockout");
                    }
                }
                await _events.RaiseAsync(new UserLoginFailureEvent(model.Username, "invalid credentials", clientId: context?.Client.ClientId));
                ModelState.AddModelError(string.Empty, AccountOptions.InvalidCredentialsErrorMessage);
            }

            // something went wrong, show form with error
            var vm = await BuildLoginViewModelAsync(model);
            return View(vm);
        }


        /// <summary>
        /// Show logout page
        /// </summary>
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> Logout(string logoutId)
        {
            // build a model so the logout page knows what to display
            var vm = await BuildLogoutViewModelAsync(logoutId);

            if (vm.ShowLogoutPrompt == false)
            {
                // if the request for logout was properly authenticated from IdentityServer, then
                // we don't need to show the prompt and can just log the user out directly.
                return await Logout(vm);
            }

            return View(vm);
        }

        /// <summary>
        /// Handle logout page postback
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout(LogoutInputModel model)
        {
            // build a model so the logged out page knows what to display
            var vm = await BuildLoggedOutViewModelAsync(model.LogoutId);

            if (User?.Identity.IsAuthenticated == true)
            {
                // delete local authentication cookie
                await _signInManager.SignOutAsync();

                // raise the logout event
                await _events.RaiseAsync(new UserLogoutSuccessEvent(User.GetSubjectId(), User.GetDisplayName()));
            }

            // check if we need to trigger sign-out at an upstream identity provider
            if (vm.TriggerExternalSignout)
            {
                // build a return URL so the upstream provider will redirect back
                // to us after the user has logged out. this allows us to then
                // complete our single sign-out processing.
                string url = Url.Action("Logout", new { logoutId = vm.LogoutId });

                // this triggers a redirect to the external provider for sign-out
                return SignOut(new AuthenticationProperties { RedirectUri = url }, vm.ExternalAuthenticationScheme);
            }

            return View("LoggedOut", vm);
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> ConfirmEmail(string userId, string code)
        {
            if (userId == null || code == null)
            {
                return View("Error");
            }
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return View("Error");
            }

            code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));

            var result = await _userManager.ConfirmEmailAsync(user, code);
            return View(result.Succeeded ? "ConfirmEmail" : "Error");
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ForgotPassword()
        {
            return View(new ForgotPasswordViewModel());
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
        {
            if (ModelState.IsValid)
            {
                TUser user = null;
                switch (model.Policy)
                {
                    case LoginResolutionPolicy.Email:
                        try
                        {
                            user = await _userManager.FindByEmailAsync(model.Email);
                        }
                        catch (Exception ex)
                        {
                            // in case of multiple users with the same email this method would throw and reveal that the email is registered
                            _logger.LogError("Error retrieving user by email ({0}) for forgot password functionality: {1}", model.Email, ex.Message);
                            user = null;
                        }
                        break;
                    case LoginResolutionPolicy.Username:
                        try
                        {
                            user = await _userManager.FindByNameAsync(model.Username);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError("Error retrieving user by userName ({0}) for forgot password functionality: {1}", model.Username, ex.Message);
                            user = null;
                        }
                        break;
                }

                if (user == null || !await _userManager.IsEmailConfirmedAsync(user))
                {
                    // Don't reveal that the user does not exist
                    return View("ForgotPasswordConfirmation");
                }

                var code = await _userManager.GeneratePasswordResetTokenAsync(user);
                code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
                var callbackUrl = Url.Action("ResetPassword", "Account", new { userId = user.Id, code }, HttpContext.Request.Scheme);

                await _emailSender.SendEmailAsync(user.Email, _localizer["ResetPasswordTitle"], _localizer["ResetPasswordBody", HtmlEncoder.Default.Encode(callbackUrl)]);

                return View("ForgotPasswordConfirmation");
            }

            return View(model);
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ResetPassword(string code = null)
        {
            return code == null ? View("Error") : View();
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                // Don't reveal that the user does not exist
                return RedirectToAction(nameof(ResetPasswordConfirmation), "Account");
            }

            var code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(model.Code));
            var result = await _userManager.ResetPasswordAsync(user, code, model.Password);

            if (result.Succeeded)
            {
                return RedirectToAction(nameof(ResetPasswordConfirmation), "Account");
            }

            AddErrors(result);

            return View();
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ResetPasswordConfirmation()
        {
            return View();
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ForgotPasswordConfirmation()
        {
            return View();
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> ExternalLoginCallback(string returnUrl = null, string remoteError = null)
        {
            if (remoteError != null)
            {
                ModelState.AddModelError(string.Empty, _localizer["ErrorExternalProvider", remoteError]);

                return View(nameof(Login));
            }
            var info = await _signInManager.GetExternalLoginInfoAsync();

            if (info == null)
            {
                return RedirectToAction(nameof(Login));
            }

            var existingUser = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            if (existingUser != null)
            {
                try
                {
                    await EnsureLoginAllowedAsync(existingUser);
                    var context = await _interaction.GetAuthorizationContextAsync(returnUrl);
                    await EnsureClientAllowedAsync(existingUser, context);
                }
                catch (SecurityException ex)
                {
                    var redirectResult = await TryTenantAdminRedirectViewAsync(existingUser, await _interaction.GetAuthorizationContextAsync(returnUrl));
                    if (redirectResult != null)
                    {
                        return redirectResult;
                    }

                    ModelState.AddModelError(string.Empty, ex.Message);
                    var forbiddenVm = await BuildLoginViewModelAsync(returnUrl);
                    return View(nameof(Login), forbiddenVm);
                }
            }

            // 1) Validate tenant <-> Okta branch_code
            var branch = GetBranchCodeOrThrow(info.Principal);
            _tenantUserValidator.EnsureBranchMatchesTenant(branch);
            var tenantKey = _tenantAccessor.Current?.TenantKey
                ?? throw new InvalidOperationException("Tenant not resolved");

            // Sign in the user with this external login provider if the user already has a login.
            var result = await _signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false);
            if (result.Succeeded)
            {
                return await RedirectToLocalAsync(returnUrl);
            }
            if (result.RequiresTwoFactor)
            {
                return RedirectToAction(nameof(LoginWith2fa), new { ReturnUrl = returnUrl });
            }
            if (result.IsLockedOut)
            {
                return View("Lockout");
            }

            // If the user does not have an account, then ask the user to create an account.
            ViewData["ReturnUrl"] = returnUrl;
            ViewData["LoginProvider"] = info.LoginProvider;
            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            var userName = info.Principal.Identity.Name;

            return View("ExternalLoginConfirmation", new ExternalLoginConfirmationViewModel { Email = email, UserName = userName });
        }

        [HttpPost]
        [HttpGet]
        [AllowAnonymous]
        public IActionResult ExternalLogin(string provider, string returnUrl = null)
        {
            // Request a redirect to the external login provider.
            var redirectUrl = Url.Action("ExternalLoginCallback", "Account", new { ReturnUrl = returnUrl });
            var properties = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);

            return Challenge(properties, provider);
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ExternalLoginConfirmation(ExternalLoginConfirmationViewModel model, string returnUrl = null)
        {
            returnUrl = returnUrl ?? Url.Content("~/");

            var info = await _signInManager.GetExternalLoginInfoAsync();
            if (info == null)
            {
                return View("ExternalLoginFailure");
            }

            var existingUser = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            if (existingUser != null)
            {
                try
                {
                    await EnsureLoginAllowedAsync(existingUser);
                    var context = await _interaction.GetAuthorizationContextAsync(returnUrl);
                    await EnsureClientAllowedAsync(existingUser, context);
                }
                catch (SecurityException ex)
                {
                    var redirectResult = await TryTenantAdminRedirectViewAsync(existingUser, await _interaction.GetAuthorizationContextAsync(returnUrl));
                    if (redirectResult != null)
                    {
                        return redirectResult;
                    }

                    ModelState.AddModelError(string.Empty, ex.Message);
                    var forbiddenVm = await BuildLoginViewModelAsync(returnUrl);
                    return View(nameof(Login), forbiddenVm);
                }
            }

            // Validate tenant against Okta branch_code
            var branch = GetBranchCodeOrThrow(info.Principal);
            _tenantUserValidator.EnsureBranchMatchesTenant(branch);
            var tenantKey = _tenantAccessor.Current?.TenantKey
                ?? throw new InvalidOperationException("Tenant not resolved");

            if (ModelState.IsValid)
            {
                var user = new TUser
                {
                    UserName = model.UserName,
                    Email = model.Email
                };

                if (user is UserIdentity tenantUser)
                {
                    tenantUser.TenantKey = tenantKey;
                    tenantUser.BranchCode = branch;
                }
                else
                {
                    throw new InvalidOperationException("TUser must implement ITenantUser for multi-tenant setup.");
                }

                var result = await _userManager.CreateAsync(user);
                if (result.Succeeded)
                {
                    result = await _userManager.AddLoginAsync(user, info);
                    if (result.Succeeded)
                    {
                        await _signInManager.SignInAsync(user, isPersistent: false);
                        return await RedirectToLocalAsync(returnUrl);
                    }
                }

                AddErrors(result);
            }

            ViewData["LoginProvider"] = info.LoginProvider;
            ViewData["ReturnUrl"] = returnUrl;
            return View(model);
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> LoginWithRecoveryCode(string returnUrl = null)
        {
            // Ensure the user has gone through the username & password screen first
            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user == null)
            {
                throw new InvalidOperationException(_localizer["Unable2FA"]);
            }

            var model = new LoginWithRecoveryCodeViewModel()
            {
                ReturnUrl = returnUrl
            };

            return View(model);
        }

        [HttpPost]
        [AllowAnonymous]
        public async Task<IActionResult> LoginWithRecoveryCode(LoginWithRecoveryCodeViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user == null)
            {
                throw new InvalidOperationException(_localizer["Unable2FA"]);
            }

            var recoveryCode = model.RecoveryCode.Replace(" ", string.Empty);

            var result = await _signInManager.TwoFactorRecoveryCodeSignInAsync(recoveryCode);

            if (result.Succeeded)
            {
                await _events.RaiseAsync(new UserLoginSuccessEvent(user.UserName, user.Id.ToString(), user.UserName));
                return LocalRedirect(string.IsNullOrEmpty(model.ReturnUrl) ? "~/" : model.ReturnUrl);
            }

            if (result.IsLockedOut)
            {
                return View("Lockout");
            }

            ModelState.AddModelError(string.Empty, _localizer["InvalidRecoveryCode"]);

            return View(model);
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> LoginWith2fa(bool rememberMe, string returnUrl = null)
        {
            // Ensure the user has gone through the username & password screen first
            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();

            if (user == null)
            {
                throw new InvalidOperationException(_localizer["Unable2FA"]);
            }

            var model = new LoginWith2faViewModel()
            {
                ReturnUrl = returnUrl,
                RememberMe = rememberMe
            };

            return View(model);
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LoginWith2fa(LoginWith2faViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user == null)
            {
                throw new InvalidOperationException(_localizer["Unable2FA"]);
            }

            var authenticatorCode = model.TwoFactorCode.Replace(" ", string.Empty).Replace("-", string.Empty);

            var result = await _signInManager.TwoFactorAuthenticatorSignInAsync(authenticatorCode, model.RememberMe, model.RememberMachine);

            if (result.Succeeded)
            {
                await _events.RaiseAsync(new UserLoginSuccessEvent(user.UserName, user.Id.ToString(), user.UserName));
                return LocalRedirect(string.IsNullOrEmpty(model.ReturnUrl) ? "~/" : model.ReturnUrl);
            }

            if (result.IsLockedOut)
            {
                return View("Lockout");
            }

            ModelState.AddModelError(string.Empty, _localizer["InvalidAuthenticatorCode"]);

            return View(model);
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult Register(string returnUrl = null)
        {
            if (!_registerConfiguration.Enabled) return View("RegisterFailure");

            ViewData["ReturnUrl"] = returnUrl;

            return _loginConfiguration.ResolutionPolicy switch
            {
                LoginResolutionPolicy.Username => View(),
                LoginResolutionPolicy.Email => View("RegisterWithoutUsername"),
                _ => View("RegisterFailure")
            };
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterViewModel model, string returnUrl = null, bool IsCalledFromRegisterWithoutUsername = false)
        {
            if (!_registerConfiguration.Enabled) return View("RegisterFailure");

            returnUrl ??= Url.Content("~/");

            ViewData["ReturnUrl"] = returnUrl;

            if (!ModelState.IsValid) return View(model);

            var user = new TUser
            {
                UserName = model.UserName,
                Email = model.Email
            };

            var result = await _userManager.CreateAsync(user, model.Password);
            if (result.Succeeded)
            {
                var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
                code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
                var callbackUrl = Url.Action("ConfirmEmail", "Account", new { userId = user.Id, code }, HttpContext.Request.Scheme);

                await _emailSender.SendEmailAsync(model.Email, _localizer["ConfirmEmailTitle"], _localizer["ConfirmEmailBody", HtmlEncoder.Default.Encode(callbackUrl)]);

                if (_identityOptions.SignIn.RequireConfirmedAccount)
                {
                    return View("RegisterConfirmation");
                }
                else
                {
                    await _signInManager.SignInAsync(user, isPersistent: false);
                    return LocalRedirect(returnUrl);
                }
            }

            AddErrors(result);

            // If we got this far, something failed, redisplay form
            if (IsCalledFromRegisterWithoutUsername)
            {
                var registerWithoutUsernameModel = new RegisterWithoutUsernameViewModel
                {
                    Email = model.Email,
                    Password = model.Password,
                    ConfirmPassword = model.ConfirmPassword
                };

                return View("RegisterWithoutUsername", registerWithoutUsernameModel);
            }
            else
            {
                return View(model);
            }
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegisterWithoutUsername(RegisterWithoutUsernameViewModel model, string returnUrl = null)
        {
            var registerModel = new RegisterViewModel
            {
                UserName = model.Email,
                Email = model.Email,
                Password = model.Password,
                ConfirmPassword = model.ConfirmPassword
            };

            return await Register(registerModel, returnUrl, true);
        }

        /*****************************************/
        /* helper APIs for the AccountController */
        /*****************************************/
        private async Task<IActionResult> RedirectToLocalAsync(string returnUrl)
        {
            if (Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            var tenantRedirect = await GetTenantRedirectUrlAsync();
            if (TryGetValidatedTenantReturnUrl(returnUrl, tenantRedirect, out var validatedReturnUrl))
            {
                return Redirect(validatedReturnUrl);
            }

            if (!string.IsNullOrWhiteSpace(tenantRedirect))
            {
                return Redirect(tenantRedirect);
            }

            return RedirectToAction(nameof(HomeController.Index), "Home");
        }

        private static bool TryGetValidatedTenantReturnUrl(
            string? requestedReturnUrl,
            string? tenantRedirectUrl,
            out string validatedReturnUrl)
        {
            validatedReturnUrl = string.Empty;

            if (!IsAbsoluteHttpUrl(requestedReturnUrl) || !IsAbsoluteHttpUrl(tenantRedirectUrl))
            {
                return false;
            }

            var requestedUri = new Uri(requestedReturnUrl!, UriKind.Absolute);
            var tenantUri = new Uri(tenantRedirectUrl!, UriKind.Absolute);
            if (!string.Equals(requestedUri.Scheme, tenantUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(requestedUri.Host, tenantUri.Host, StringComparison.OrdinalIgnoreCase) ||
                requestedUri.Port != tenantUri.Port)
            {
                return false;
            }

            var tenantBasePath = NormalizePathPrefix(tenantUri.AbsolutePath);
            var requestedPath = NormalizePathPrefix(requestedUri.AbsolutePath);
            if (!requestedPath.StartsWith(tenantBasePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            validatedReturnUrl = requestedReturnUrl!;
            return true;
        }

        private static bool IsAbsoluteHttpUrl(string? value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static string NormalizePathPrefix(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "/")
            {
                return "/";
            }

            return path.EndsWith("/", StringComparison.Ordinal) ? path : $"{path}/";
        }
        private async Task<string?> GetTenantRedirectUrlAsync()
        {
            var tenantKey = _tenantAccessor.Current?.TenantKey;
            if (string.IsNullOrWhiteSpace(tenantKey)) return null;

            var tenant = await _tenantStore.FindAsync(tenantKey, HttpContext.RequestAborted);
            if (tenant?.RedirectUrl == null) return null;

            if (Uri.TryCreate(tenant.RedirectUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                return tenant.RedirectUrl;
            }

            return null;
        }

        private void AddErrors(IdentityResult result)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }
        }

        private async Task<LoginViewModel> BuildLoginViewModelAsync(string returnUrl)
        {
            var context = await _interaction.GetAuthorizationContextAsync(returnUrl);
            if (context?.IdP != null && await _schemeProvider.GetSchemeAsync(context.IdP) != null)
            {
                var local = context.IdP == IdentityServerConstants.LocalIdentityProvider;

                // this is meant to short circuit the UI and only trigger the one external IdP
                var vm = new LoginViewModel
                {
                    EnableLocalLogin = local,
                    HasTenantContext = _tenantAccessor.Current != null,
                    ReturnUrl = returnUrl,
                    Username = context?.LoginHint,
                };

                if (!local)
                {
                    vm.ExternalProviders = new[] { new ExternalProvider { AuthenticationScheme = context.IdP } };
                }

                return vm;
            }

            var schemes = await _schemeProvider.GetAllSchemesAsync();

            var providers = schemes
                .Where(x => x.DisplayName != null)
                .Select(x => new ExternalProvider
                {
                    DisplayName = x.DisplayName ?? x.Name,
                    AuthenticationScheme = x.Name
                }).ToList();

            var dynamicSchemes = (await _identityProviderStore.GetAllSchemeNamesAsync())
                .Where(x => x.Enabled)
                .Select(x => new ExternalProvider
                {
                    AuthenticationScheme = x.Scheme,
                    DisplayName = x.DisplayName
                });

            providers.AddRange(dynamicSchemes);

            var allowLocal = true;
            if (context?.Client.ClientId != null)
            {
                var client = await _clientStore.FindEnabledClientByIdAsync(context.Client.ClientId);
                if (client != null)
                {
                    allowLocal = client.EnableLocalLogin;

                    if (client.IdentityProviderRestrictions != null && client.IdentityProviderRestrictions.Any())
                    {
                        providers = providers.Where(provider => client.IdentityProviderRestrictions.Contains(provider.AuthenticationScheme)).ToList();
                    }
                }
            }

            return new LoginViewModel
            {
                AllowRememberLogin = AccountOptions.AllowRememberLogin,
                EnableLocalLogin = allowLocal && AccountOptions.AllowLocalLogin,
                HasTenantContext = _tenantAccessor.Current != null,
                ReturnUrl = returnUrl,
                Username = context?.LoginHint,
                ExternalProviders = providers.ToArray()
            };
        }

        private async Task<LoginViewModel> BuildLoginViewModelAsync(LoginInputModel model)
        {
            var vm = await BuildLoginViewModelAsync(model.ReturnUrl);
            vm.Username = model.Username;
            vm.RememberLogin = model.RememberLogin;
            return vm;
        }

        private async Task<LogoutViewModel> BuildLogoutViewModelAsync(string logoutId)
        {
            var vm = new LogoutViewModel { LogoutId = logoutId, ShowLogoutPrompt = AccountOptions.ShowLogoutPrompt };

            if (User?.Identity.IsAuthenticated != true)
            {
                // if the user is not authenticated, then just show logged out page
                vm.ShowLogoutPrompt = false;
                return vm;
            }

            var context = await _interaction.GetLogoutContextAsync(logoutId);
            if (context?.ShowSignoutPrompt == false)
            {
                // it's safe to automatically sign-out
                vm.ShowLogoutPrompt = false;
                return vm;
            }

            // show the logout prompt. this prevents attacks where the user
            // is automatically signed out by another malicious web page.
            return vm;
        }

        private async Task<LoggedOutViewModel> BuildLoggedOutViewModelAsync(string logoutId)
        {
            // get context information (client name, post logout redirect URI and iframe for federated signout)
            var logout = await _interaction.GetLogoutContextAsync(logoutId);

            var vm = new LoggedOutViewModel
            {
                AutomaticRedirectAfterSignOut = AccountOptions.AutomaticRedirectAfterSignOut,
                PostLogoutRedirectUri = logout?.PostLogoutRedirectUri,
                ClientName = string.IsNullOrEmpty(logout?.ClientName) ? logout?.ClientId : logout?.ClientName,
                SignOutIframeUrl = logout?.SignOutIFrameUrl,
                LogoutId = logoutId
            };

            if (User?.Identity.IsAuthenticated == true)
            {
                var idp = User.FindFirst(JwtClaimTypes.IdentityProvider)?.Value;
                if (idp != null && idp != IdentityServerConstants.LocalIdentityProvider)
                {
                    var providerSupportsSignout = await HttpContext.GetSchemeSupportsSignOutAsync(idp);
                    if (providerSupportsSignout)
                    {
                        if (vm.LogoutId == null)
                        {
                            // if there's no current logout context, we need to create one
                            // this captures necessary info from the current logged in user
                            // before we signout and redirect away to the external IdP for signout
                            vm.LogoutId = await _interaction.CreateLogoutContextAsync();
                        }

                        vm.ExternalAuthenticationScheme = idp;
                    }
                }
            }

            return vm;
        }

        private async Task EnsureLoginAllowedAsync(TUser user)
        {
            var tenant = _tenantAccessor.Current;

            var isSuperAdmin = await _userManager.IsInRoleAsync(user, _adminConfiguration.AdministrationRole);
            var isTenantAdmin = !string.IsNullOrWhiteSpace(_adminConfiguration.TenantAdminRole) &&
                                await _userManager.IsInRoleAsync(user, _adminConfiguration.TenantAdminRole);

            if (tenant == null)
            {
                if (!isSuperAdmin)
                    throw new SecurityException("Tenant admin cannot login on global host.");

                return;
            }

            if (isSuperAdmin && !_adminConfiguration.AllowSuperAdminOnTenantHost)
                throw new SecurityException("Super admin cannot login on tenant host.");

            if (!isSuperAdmin && !isTenantAdmin)
                throw new SecurityException("User is not allowed to login for tenant.");

            if (isTenantAdmin && user is UserIdentity tenantUser)
            {
                _tenantUserValidator.EnsureUserBelongsToTenant(tenantUser.TenantKey);
            }
        }

        private async Task EnsureClientAllowedAsync(TUser user, AuthorizationRequest? context)
        {
            if (context == null) return;

            if (!IsAdminUiClient(context)) return;

            var isTenantAdmin = !string.IsNullOrWhiteSpace(_adminConfiguration.TenantAdminRole) &&
                                await _userManager.IsInRoleAsync(user, _adminConfiguration.TenantAdminRole);

            if (isTenantAdmin)
                throw new SecurityException("Tenant admin cannot login to admin UI.");
        }

        private async Task<IActionResult?> TryTenantAdminRedirectViewAsync(TUser user, AuthorizationRequest? context)
        {
            if (context == null) return null;

            if (!IsAdminUiClient(context)) return null;

            var isTenantAdmin = !string.IsNullOrWhiteSpace(_adminConfiguration.TenantAdminRole) &&
                                await _userManager.IsInRoleAsync(user, _adminConfiguration.TenantAdminRole);
            if (!isTenantAdmin) return null;

            var redirectUrl = await GetTenantRedirectUrlAsync();
            if (string.IsNullOrWhiteSpace(redirectUrl)) return null;

            return View("TenantRedirect", new RedirectViewModel { RedirectUrl = redirectUrl });
        }

        private bool IsAdminUiClient(AuthorizationRequest context)
        {
            var adminClientId = _adminConfiguration.IdentityAdminClientId;
            if (!string.IsNullOrWhiteSpace(adminClientId) &&
                string.Equals(context.Client.ClientId, adminClientId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var baseUrl = _adminConfiguration.IdentityAdminBaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl)) return false;

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)) return false;
            if (!Uri.TryCreate(context.RedirectUri, UriKind.Absolute, out var requestedRedirectUri)) return false;

            if (!string.Equals(requestedRedirectUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
                return false;

            if (baseUri.IsDefaultPort) return true;

            return requestedRedirectUri.Port == baseUri.Port;
        }

        private string GetBranchCodeOrThrow(ClaimsPrincipal principal)
        {
            // Okta custom claim b?n d?t là branch_code
            var branch = principal.FindFirst("branch_code")?.Value;

            // fallback n?u b?n map claim khác:
            // var branch = principal.FindFirst("branch_id")?.Value;

            if (string.IsNullOrWhiteSpace(branch))
                throw new SecurityException("Missing branch_code from external provider.");

            return branch;
        }
    }
}


