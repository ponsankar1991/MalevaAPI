using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace MalevaAPI.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly IDistributedCache _cache;
        private readonly ILogger<AuthController> _logger;

        public AuthController(IConfiguration configuration, IDistributedCache cache, ILogger<AuthController> logger)
        {
            _configuration = configuration;
            _cache = cache;
            _logger = logger;
        }

        public class LoginRequest
        {
            public string? Username { get; set; }
            public string? Password { get; set; }
        }

        public class LoginResponse
        {
            public string Token { get; set; } = default!;
            public DateTime Expires { get; set; }
        }

        // GET /Auth/welcome
        // Requires a valid JWT. Returns a simple welcome message including the username from the token.
        [HttpGet("welcome")]
        [Authorize]
        public IActionResult Welcome()
        {
            var username = User?.Identity?.Name ?? User?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value ?? "user";
            return Ok(new { Message = $"Welcome, {username}!" });
        }

        // POST /Auth/logout
        // Invalidates the current JWT by removing it from the distributed cache.
        [HttpPost("logout")]
        [Authorize]
        public async Task<IActionResult> Logout()
        {
            // extract jti from token
            var jti = User?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;
            if (string.IsNullOrEmpty(jti))
            {
                return BadRequest("Token does not contain jti.");
            }

            var cacheKey = $"tokens:{jti}";
            try
            {
                await _cache.RemoveAsync(cacheKey);

                // Also remove user->jti mapping if present
                var username = User?.Identity?.Name ?? User?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
                if (!string.IsNullOrEmpty(username))
                {
                    var userTokenKey = $"user:{username}:token";
                    await _cache.RemoveAsync(userTokenKey);
                }
            }
            catch (Exception ex)
            {
                // log and continue
                _logger?.LogWarning(ex, "Failed to remove token from cache during logout.");
                // return 500? We'll return 200 to ensure logout from client perspective works even if cache removal failed.
            }

            return Ok(new { Message = "Logged out" });
        }

        // POST /Auth/login
        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (request?.Username is null || request.Password is null)
                return BadRequest("Username and password required.");

            // TODO: Replace this with your real user validation (DB, identity, external provider)
            var isValidUser = ValidateCredentials(request.Username, request.Password);
            if (!isValidUser)
                return Unauthorized("Invalid username or password.");

            // create JWT
            var jwtKey = _configuration["Jwt:Key"]
                         ?? throw new InvalidOperationException("JWT key is not configured.");
            var jwtIssuer = _configuration["Jwt:Issuer"];
            var jwtAudience = _configuration["Jwt:Audience"];
            var expireMinutes = int.TryParse(_configuration["Jwt:ExpireMinutes"], out var m) ? m : 60;

            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(jwtKey);

            var jti = Guid.NewGuid().ToString(); // token id
            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, request.Username),
                new Claim(JwtRegisteredClaimNames.Jti, jti),
                new Claim(ClaimTypes.Name, request.Username)
            };

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddMinutes(expireMinutes),
                Issuer = jwtIssuer,
                Audience = jwtAudience,
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256)
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            var tokenString = tokenHandler.WriteToken(token);

            // store token in Redis with TTL matching token expiry
            var cacheKey = $"tokens:{jti}";
            var absoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(expireMinutes);
            var cacheOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = absoluteExpiration
            };

            try
            {
                await _cache.SetStringAsync(cacheKey, tokenString, cacheOptions);

                // Optionally: store a mapping from username -> jti so you can revoke previous tokens, etc.
                var userTokenKey = $"user:{request.Username}:token";
                await _cache.SetStringAsync(userTokenKey, jti, cacheOptions);
            }
            catch (Exception ex)
            {
                // If Redis (or other distributed cache) is unavailable, do not fail the login flow.
                // Log and continue — tokens will still be returned but won't be stored server-side.
                _logger?.LogWarning(ex, "Failed to store token in distributed cache. Continuing without cache.");
            }

            var response = new LoginResponse
            {
                Token = tokenString,
                Expires = tokenDescriptor.Expires!.Value.ToUniversalTime()
            };

            return Ok(response);
        }

        // Very simple demo validator. Replace with real user validation.
        private static bool ValidateCredentials(string username, string password)
        {
            // Example: allow a single demo account
            return username == "admin" && password == "password";
        }
    }
}
