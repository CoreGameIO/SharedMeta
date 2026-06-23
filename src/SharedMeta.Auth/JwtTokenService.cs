using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace SharedMeta.Auth
{
    /// <summary>
    /// Service for generating and validating JWT tokens.
    /// </summary>
    public class JwtTokenService
    {
        private readonly MetaAuthOptions _options;
        private readonly SigningCredentials _signingCredentials;

        public MetaAuthOptions Options => _options;

        public JwtTokenService(MetaAuthOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            if (string.IsNullOrEmpty(options.SecretKey) || options.SecretKey.Length < 32)
                throw new ArgumentException("SecretKey must be at least 32 characters", nameof(options));

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SecretKey));
            _signingCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        }

        /// <summary>
        /// Generate a JWT token for a player.
        /// </summary>
        public string GenerateToken(string playerId, string authType = "device")
        {
            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, playerId),
                new Claim("auth_type", authType),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: _options.Issuer,
                audience: _options.Audience,
                claims: claims,
                expires: DateTime.UtcNow + _options.AccessTokenLifetime,
                signingCredentials: _signingCredentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
