using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace Concert_Viewer.Services
{
    public class AuthenticationService
    {
        private readonly ILogger<AuthenticationService> _logger;
        private readonly List<User> _users;

        public AuthenticationService(IConfiguration configuration, ILogger<AuthenticationService> logger)
        {
            _logger = logger;

            var authConfig = configuration.GetSection("Authentication").Get<AuthenticationConfig>();
            _users = authConfig?.Users ?? new List<User>();
        }

        public bool HasUsers => _users.Count > 0;

        public ClaimsPrincipal? CreatePrincipal(string? username, string? password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                return null;
            }

            var user = _users.FirstOrDefault(u =>
                u.Username.Equals(username, StringComparison.OrdinalIgnoreCase) &&
                u.Password == password);

            if (user is null)
            {
                _logger.LogWarning("Login failed for username {Username}", username);
                return null;
            }

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim("LoginTime", DateTime.UtcNow.ToString("O"))
            };

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

            _logger.LogInformation("Validated credentials for user {Username}", user.Username);
            return new ClaimsPrincipal(claimsIdentity);
        }

        public AuthenticationProperties CreateAuthenticationProperties()
        {
            return new AuthenticationProperties
            {
                IsPersistent = true,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
            };
        }

        public string NormalizeReturnUrl(string? returnUrl)
        {
            if (string.IsNullOrWhiteSpace(returnUrl))
            {
                return "/";
            }

            if (Uri.TryCreate(returnUrl, UriKind.Relative, out var relativeUrl) &&
                returnUrl.StartsWith('/'))
            {
                return relativeUrl.ToString();
            }

            return "/";
        }
    }

    public class CustomAuthenticationStateProvider : AuthenticationStateProvider
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public CustomAuthenticationStateProvider(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var httpContext = _httpContextAccessor.HttpContext;
            
            if (httpContext?.User?.Identity?.IsAuthenticated == true)
            {
                return Task.FromResult(new AuthenticationState(httpContext.User));
            }

            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
        }
    }

    public class User
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Role { get; set; } = "User";
    }

    public class AuthenticationConfig
    {
        public List<User> Users { get; set; } = new List<User>();
    }
}
