using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

namespace Concert_Viewer.Services
{
    public class AuthenticationService
    {
        private readonly IConfiguration _configuration;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly List<User> _users;

        public AuthenticationService(IConfiguration configuration, IHttpContextAccessor httpContextAccessor)
        {
            _configuration = configuration;
            _httpContextAccessor = httpContextAccessor;
            
            var authConfig = _configuration.GetSection("Authentication").Get<AuthenticationConfig>();
            _users = authConfig?.Users ?? new List<User>();
        }

        public async Task<bool> LoginAsync(string username, string password)
        {
            var user = _users.FirstOrDefault(u => 
                u.Username.Equals(username, StringComparison.OrdinalIgnoreCase) && 
                u.Password == password);
                
            if (user != null && _httpContextAccessor.HttpContext != null)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, username),
                    new Claim("LoginTime", DateTime.UtcNow.ToString("O"))
                };

                var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var authProperties = new AuthenticationProperties
                {
                    IsPersistent = true, // Creates a persistent cookie
                    
                    AllowRefresh = true
                };

                await _httpContextAccessor.HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(claimsIdentity),
                    authProperties);

                Console.WriteLine($"✅ User {username} logged in via cookie authentication");
                return true;
            }
            
            Console.WriteLine($"❌ Login failed for username: {username}");
            return false;
        }

        public async Task LogoutAsync()
        {
            if (_httpContextAccessor.HttpContext != null)
            {
                var username = _httpContextAccessor.HttpContext.User.Identity?.Name;
                
                await _httpContextAccessor.HttpContext.SignOutAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme);
                
                Console.WriteLine($"✅ User {username} logged out");
            }
        }

        public string? GetCurrentUser()
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext?.User?.Identity?.IsAuthenticated == true)
            {
                var username = httpContext.User.Identity.Name;
                Console.WriteLine($"✅ Current user from cookie: {username}");
                return username;
            }
            
            Console.WriteLine("❌ No authenticated user found");
            return null;
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
                var username = httpContext.User.Identity.Name;
                Console.WriteLine($"✅ User authenticated via cookie: {username}");
                return Task.FromResult(new AuthenticationState(httpContext.User));
            }

            Console.WriteLine("❌ No authenticated user - returning anonymous");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
        }

        public void NotifyAuthenticationStateChanged()
        {
            Console.WriteLine("🔔 Notifying authentication state change");
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
    }

    public class User
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class AuthenticationConfig
    {
        public List<User> Users { get; set; } = new List<User>();
    }
}