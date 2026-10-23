using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Authentication;
using Autodesk.Authentication.Model;

public partial class APS
{
    private string _appToken;
    private DateTime _appTokenExpiresAt;
    private readonly SemaphoreSlim _appTokenLock = new(1, 1);

    public string GetAuthorizationURL()
    {
        var authenticationClient = new AuthenticationClient();
        return authenticationClient.Authorize(_clientId, ResponseType.Code, _callbackUri, InternalTokenScopes);
    }

    public async Task<Tokens> GenerateTokens(string code)
    {
        var authenticationClient = new AuthenticationClient();
        var internalAuth = await authenticationClient.GetThreeLeggedTokenAsync(_clientId, code, _callbackUri, clientSecret: _clientSecret);
        var publicAuth = await authenticationClient.RefreshTokenAsync(internalAuth.RefreshToken, _clientId, clientSecret: _clientSecret, scopes: PublicTokenScopes);
        return new Tokens
        {
            PublicToken = publicAuth.AccessToken,
            InternalToken = internalAuth.AccessToken,
            RefreshToken = publicAuth.RefreshToken,
            ExpiresAt = DateTime.Now.ToUniversalTime().AddSeconds((double)internalAuth.ExpiresIn)
        };
    }

    // Refresh tokens are single-use: when several requests carrying the same expired cookies arrive together (viewer
    // token, tree, job polling), only one refresh may happen. The others, and requests arriving shortly after with the
    // old cookies, reuse its result instead of failing (which left the viewer loading with an expired token).
    private static readonly ConcurrentDictionary<string, (Lazy<Task<Tokens>> Refresh, DateTime StartedAt)> _refreshes = new();

    public Task<Tokens> RefreshTokensOnce(Tokens tokens)
    {
        var now = DateTime.UtcNow;
        foreach (var stale in _refreshes.Where(entry => now - entry.Value.StartedAt > TimeSpan.FromMinutes(60)).ToList())
        {
            _refreshes.TryRemove(stale.Key, out _);
        }
        var key = tokens.RefreshToken;
        // Lazy: GetOrAdd may call the factory more than once under contention, but only one refresh may start
        var entry = _refreshes.GetOrAdd(key, _ => (new Lazy<Task<Tokens>>(() => RefreshTokens(tokens)), now));
        var refresh = entry.Refresh.Value;
        // Don't hand out failures (e.g., a network error): the next request retries
        refresh.ContinueWith(task => _refreshes.TryRemove(key, out _), TaskContinuationOptions.OnlyOnFaulted);
        return refresh;
    }

    public async Task<Tokens> RefreshTokens(Tokens tokens)
    {
        var authenticationClient = new AuthenticationClient();
        var internalAuth = await authenticationClient.RefreshTokenAsync(tokens.RefreshToken, _clientId, clientSecret: _clientSecret, scopes: InternalTokenScopes);
        // The first refresh already used the refresh token that the browser holds, so a failure of this second one
        // (e.g., a network error) is retried once: giving up here would lose the session
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var publicAuth = await authenticationClient.RefreshTokenAsync(internalAuth.RefreshToken, _clientId, clientSecret: _clientSecret, scopes: PublicTokenScopes);
                return new Tokens
                {
                    PublicToken = publicAuth.AccessToken,
                    InternalToken = internalAuth.AccessToken,
                    RefreshToken = publicAuth.RefreshToken,
                    ExpiresAt = DateTime.Now.ToUniversalTime().AddSeconds((double)internalAuth.ExpiresIn)
                };
            }
            catch (Exception) when (attempt < 2)
            {
                await Task.Delay(500);
            }
        }
    }

    public async Task<UserInfo> GetUserProfile(Tokens tokens)
    {
        var authenticationClient = new AuthenticationClient();
        UserInfo userInfo = await authenticationClient.GetUserInfoAsync(tokens.InternalToken);
        return userInfo;
    }

    // 2-legged token used for Design Automation and for the app-owned bucket
    public async Task<string> GetAppToken()
    {
        await _appTokenLock.WaitAsync();
        try
        {
            if (_appToken == null || _appTokenExpiresAt < DateTime.Now.ToUniversalTime().AddMinutes(5))
            {
                var authenticationClient = new AuthenticationClient();
                var auth = await authenticationClient.GetTwoLeggedTokenAsync(_clientId, _clientSecret, AppTokenScopes);
                _appToken = auth.AccessToken;
                _appTokenExpiresAt = DateTime.Now.ToUniversalTime().AddSeconds((double)auth.ExpiresIn);
            }
            return _appToken;
        }
        finally
        {
            _appTokenLock.Release();
        }
    }
}
