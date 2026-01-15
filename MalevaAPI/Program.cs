using System.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
// Register IHttpClientFactory so controllers can inject IHttpClientFactory
builder.Services.AddHttpClient();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Redis distributed cache
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration["Redis:Configuration"] ?? "localhost:6379";
    options.InstanceName = builder.Configuration["Redis:InstanceName"] ?? "MalevaAPI:";
});

// JWT Authentication
var jwtKey = builder.Configuration["Jwt:Key"];
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var jwtAudience = builder.Configuration["Jwt:Audience"];
if (!string.IsNullOrEmpty(jwtKey))
{
    var keyBytes = Encoding.UTF8.GetBytes(jwtKey);
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            ValidateIssuer = !string.IsNullOrEmpty(jwtIssuer),
            ValidIssuer = jwtIssuer,
            ValidateAudience = !string.IsNullOrEmpty(jwtAudience),
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        // Validate that the token has not been revoked (stored in distributed cache)
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                try
                {
                    var cache = context.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();
                    var jti = context.Principal?.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
                    if (string.IsNullOrEmpty(jti))
                    {
                        context.Fail("Token does not contain jti");
                        return;
                    }

                    var cacheKey = $"tokens:{jti}";
                    var cachedToken = await cache.GetStringAsync(cacheKey);

                    // extract raw token from the Authorization header
                    var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
                    var incomingToken = authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                        ? authHeader.Substring("Bearer ".Length).Trim()
                        : null;

                    if (string.IsNullOrEmpty(cachedToken) || string.IsNullOrEmpty(incomingToken) || !string.Equals(cachedToken, incomingToken, StringComparison.Ordinal))
                    {
                        context.Fail("Token revoked or not found in cache");
                    }
                }
                catch (Exception ex)
                {
                    // If cache check fails, optionally fail authentication or allow it. Here we fail to be conservative.
                    context.Fail($"Token validation failed: {ex.Message}");
                }
            }
        };
    });
}

builder.Services.AddAuthorization();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
