using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace WorkerService.Api;

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);

public sealed class ApiAuthentication
{
    private readonly string _username;
    private readonly byte[] _password;
    private readonly TimeSpan _tokenLifetime;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tokens = new();

    public ApiAuthentication(string username, string password, TimeSpan? tokenLifetime = null)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new ArgumentException("API authentication credentials must be configured.");

        _username = username;
        _password = Encoding.UTF8.GetBytes(password);
        _tokenLifetime = tokenLifetime ?? TimeSpan.FromHours(8);
    }

    public bool ValidateCredentials(string username, string password)
    {
        var suppliedUser = Encoding.UTF8.GetBytes(username ?? string.Empty);
        var suppliedPassword = Encoding.UTF8.GetBytes(password ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(
                   suppliedUser,
                   Encoding.UTF8.GetBytes(_username)) &&
               CryptographicOperations.FixedTimeEquals(suppliedPassword, _password);
    }

    public LoginResponse IssueToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var expiresAt = DateTimeOffset.UtcNow.Add(_tokenLifetime);
        _tokens[token] = expiresAt;
        return new LoginResponse(token, expiresAt);
    }

    public bool ValidateToken(string token)
    {
        if (!_tokens.TryGetValue(token, out var expiresAt))
            return false;
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            _tokens.TryRemove(token, out _);
            return false;
        }
        return true;
    }
}
