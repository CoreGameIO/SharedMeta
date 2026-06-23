#if UNITY_5_3_OR_NEWER
using System;
using System.Globalization;
using SharedMeta.Core.Auth;
using UnityEngine;

#nullable enable

namespace SharedMeta.Client.Auth
{
    /// <summary>
    /// Unity implementation of <see cref="ITokenStorage"/> using PlayerPrefs.
    /// Stores JWT token, player ID, and expiration across app sessions.
    /// </summary>
    public class PlayerPrefsTokenStorage : ITokenStorage
    {
        private readonly string _tokenKey;
        private readonly string _playerIdKey;
        private readonly string _expiryKey;
        private readonly string _refreshKey;
        private readonly string _refreshExpiryKey;

        /// <summary>
        /// Create token storage isolated by <see cref="Application.identifier"/> (bundle ID).
        /// Each Unity project gets its own cached token slot automatically.
        /// </summary>
        public PlayerPrefsTokenStorage() : this(scope: null) { }

        /// <summary>
        /// Create token storage isolated by both <see cref="Application.identifier"/> and the
        /// supplied <paramref name="scope"/>. Pass the deviceId here when running multiple
        /// instances of the same project on one device (e.g. dev builds with
        /// <c>UseRandomDeviceId</c>) — each unique scope gets its own PlayerPrefs slot, so a
        /// fresh deviceId can't accidentally pick up a JWT cached for a previous deviceId and
        /// reuse the wrong PlayerId. With <paramref name="scope"/> null or empty, the keys are
        /// identical to the parameterless ctor (one slot per project).
        /// </summary>
        public PlayerPrefsTokenStorage(string? scope)
        {
            var prefix = Application.identifier;
            var suffix = string.IsNullOrEmpty(scope) ? "" : "_" + Sanitize(scope!);
            _tokenKey = $"{prefix}_Auth_Token{suffix}";
            _playerIdKey = $"{prefix}_Auth_PlayerId{suffix}";
            _expiryKey = $"{prefix}_Auth_Expiry{suffix}";
            _refreshKey = $"{prefix}_Auth_Refresh{suffix}";
            _refreshExpiryKey = $"{prefix}_Auth_RefreshExpiry{suffix}";
        }

        // PlayerPrefs keys are free-form strings, but keep the scope conservative to avoid
        // surprises on platforms that round-trip prefs through registry/plist storage.
        private static string Sanitize(string s)
        {
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                    chars[i] = '_';
            }
            return new string(chars);
        }

        public CachedToken? Load()
        {
            if (!PlayerPrefs.HasKey(_tokenKey))
                return null;

            var token = PlayerPrefs.GetString(_tokenKey);
            var playerId = PlayerPrefs.GetString(_playerIdKey, "");
            var expiryStr = PlayerPrefs.GetString(_expiryKey, "");

            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(playerId))
                return null;

            if (!DateTime.TryParse(expiryStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var expiry))
                return null;

            var refreshToken = PlayerPrefs.GetString(_refreshKey, "");
            DateTime refreshExpiry = default;
            var refreshExpiryStr = PlayerPrefs.GetString(_refreshExpiryKey, "");
            if (!string.IsNullOrEmpty(refreshExpiryStr))
                DateTime.TryParse(refreshExpiryStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out refreshExpiry);

            var cached = new CachedToken(token, playerId, expiry, refreshToken, refreshExpiry);
            // Keep the token while either the access OR the refresh part is still usable.
            return (cached.IsValid || cached.RefreshValid) ? cached : null;
        }

        public void Save(CachedToken token)
        {
            PlayerPrefs.SetString(_tokenKey, token.Token);
            PlayerPrefs.SetString(_playerIdKey, token.PlayerId);
            PlayerPrefs.SetString(_expiryKey, token.ExpiresAt.ToString("O", CultureInfo.InvariantCulture));
            PlayerPrefs.SetString(_refreshKey, token.RefreshToken);
            PlayerPrefs.SetString(_refreshExpiryKey, token.RefreshExpiresAt.ToString("O", CultureInfo.InvariantCulture));
            PlayerPrefs.Save();
        }

        public void Clear()
        {
            PlayerPrefs.DeleteKey(_tokenKey);
            PlayerPrefs.DeleteKey(_playerIdKey);
            PlayerPrefs.DeleteKey(_expiryKey);
            PlayerPrefs.DeleteKey(_refreshKey);
            PlayerPrefs.DeleteKey(_refreshExpiryKey);
            PlayerPrefs.Save();
        }
    }
}
#endif
