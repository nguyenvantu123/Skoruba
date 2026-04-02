using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration.Constants;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Users;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers;

[Route("api/users/preferences")]
[ApiController]
[Authorize(Policy = AuthorizationConsts.AdministrationPolicy)]
[Produces("application/json", "application/problem+json")]
public class UserPreferencesController : ControllerBase
{
    private readonly IUserThemePreferenceService _userThemePreferenceService;

    public UserPreferencesController(IUserThemePreferenceService userThemePreferenceService)
    {
        _userThemePreferenceService = userThemePreferenceService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(ThemePreferenceApiDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ThemePreferenceApiDto>> GetPreferencesAsync()
    {
        var result = await _userThemePreferenceService.GetPreferencesAsync(User, HttpContext.RequestAborted);
        if (result == null)
        {
            return NotFound();
        }

        return Ok(result);
    }

    [HttpPut]
    [ProducesResponseType(typeof(ThemePreferenceApiDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ThemePreferenceApiDto>> UpdatePreferencesAsync([FromBody] ThemePreferenceApiDto request)
    {
        try
        {
            var result = await _userThemePreferenceService.UpdatePreferencesAsync(User, request, HttpContext.RequestAborted);
            if (result == null)
            {
                return NotFound();
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(nameof(request.Theme), ex.Message);
            return ValidationProblem(ModelState);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(
                title: "Unable to update theme preference",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    [HttpGet("theme")]
    public Task<ActionResult<ThemePreferenceApiDto>> GetThemePreferenceAsync() => GetPreferencesAsync();

    [HttpPut("theme")]
    public Task<ActionResult<ThemePreferenceApiDto>> UpdateThemePreferenceAsync([FromBody] ThemePreferenceApiDto request) => UpdatePreferencesAsync(request);
}
