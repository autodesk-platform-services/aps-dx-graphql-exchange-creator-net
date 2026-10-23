using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly APS _aps;

    public AuthController(APS aps)
    {
        _aps = aps;
    }

    public static async Task<Tokens> PrepareTokens(HttpRequest request, HttpResponse response, APS aps)
    {
        // Signed in means a refresh token: the access token may be missing (e.g., its cookie is gone) and is then renewed below
        if (!request.Cookies.ContainsKey("internal_token") && !request.Cookies.ContainsKey("refresh_token"))
        {
            return null;
        }
        var tokens = new Tokens
        {
            PublicToken = request.Cookies["public_token"],
            InternalToken = request.Cookies["internal_token"],
            RefreshToken = request.Cookies["refresh_token"],
            ExpiresAt = ParseExpiration(request.Cookies["expires_at"])
        };
        try
        {
            // Refresh a little ahead of time, so that a request never gets a token about to expire. The shared refresh may
            // hand out tokens of an earlier refresh (the browser still had the old cookies), which can be about to expire
            // themselves: then the loop refreshes again, following the chain of refresh tokens.
            var refreshed = false;
            for (var attempt = 0; attempt < 3 && (tokens.InternalToken == null || tokens.PublicToken == null || tokens.ExpiresAt < DateTime.UtcNow.AddMinutes(5)); attempt++)
            {
                tokens = await aps.RefreshTokensOnce(tokens);
                refreshed = true;
            }
            if (refreshed)
            {
                SetTokenCookies(response, tokens);
            }
        }
        catch (Exception ex)
        {
            // Typically an expired or already used refresh token. Say so in the log and treat the user as signed out (401,
            // so the pages ask for a new login) instead of failing every request with a 500.
            request.HttpContext.RequestServices.GetService<ILogger<AuthController>>()
                ?.LogWarning(ex, "Could not refresh the access token; the user has to sign in again");
            DeleteTokenCookies(response);
            // The reason travels with the 401, so that the page can show it (one line, ASCII only: it is a header)
            response.Headers["X-Auth-Error"] = new string(ex.Message.Where(c => c >= ' ' && c < 127).Take(300).ToArray());
            return null;
        }
        return tokens;
    }

    private static void DeleteTokenCookies(HttpResponse response)
    {
        response.Cookies.Delete("public_token");
        response.Cookies.Delete("internal_token");
        response.Cookies.Delete("refresh_token");
        response.Cookies.Delete("expires_at");
    }

    private static void SetTokenCookies(HttpResponse response, Tokens tokens)
    {
        response.Cookies.Append("public_token", tokens.PublicToken);
        response.Cookies.Append("internal_token", tokens.InternalToken);
        response.Cookies.Append("refresh_token", tokens.RefreshToken);
        // Round-trip format, independent of the server's culture
        response.Cookies.Append("expires_at", tokens.ExpiresAt.ToString("o", CultureInfo.InvariantCulture));
    }

    // Always read as UTC; also accepts cookies written in the previous, culture-specific format
    private static DateTime ParseExpiration(string value)
    {
        const DateTimeStyles utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, utc, out var date) || DateTime.TryParse(value, CultureInfo.CurrentCulture, utc, out date)
            ? date
            : DateTime.MinValue; // Unreadable: refresh
    }

    [HttpGet("login")]
    public ActionResult Login()
    {
        var redirectUri = _aps.GetAuthorizationURL();
        return Redirect(redirectUri);
    }

    [HttpGet("logout")]
    public ActionResult Logout()
    {
        DeleteTokenCookies(Response);
        return Redirect("/");
    }

    [HttpGet("callback")]
    public async Task<ActionResult> Callback(string code)
    {
        var tokens = await _aps.GenerateTokens(code);
        SetTokenCookies(Response, tokens);
        return Redirect("/");
    }

    [HttpGet("profile")]
    public async Task<ActionResult> GetProfile()
    {
        var tokens = await PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        var profile = await _aps.GetUserProfile(tokens);
        return Ok(new { name = profile.Name });
    }

    // Keeps the session alive: refreshes the tokens when they are about to expire, and says when they expire. The pages call it
    // every few minutes while they are open (see session.js), so the cookies are renewed without waiting for a user action.
    [HttpGet("session")]
    public async Task<ActionResult> GetSession()
    {
        var tokens = await PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        return Ok(new { expiresIn = Math.Floor((tokens.ExpiresAt - DateTime.UtcNow).TotalSeconds) });
    }

    [HttpGet("token")]
    public async Task<ActionResult> GetPublicToken()
    {
        var tokens = await PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        var expiresIn = Math.Floor((tokens.ExpiresAt - DateTime.UtcNow).TotalSeconds);
        return Ok(new { access_token = tokens.PublicToken, expires_in = expiresIn });
    }
}
