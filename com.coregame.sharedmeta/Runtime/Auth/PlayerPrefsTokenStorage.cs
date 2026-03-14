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

        /// <summary>
        /// Create token storage isolated by Application.identifier (bundle ID).
        /// Each Unity project gets its own cached token automatically.
        /// </summary>
        public PlayerPrefsTokenStorage()
        {
            var prefix = Application.identifier;
            _tokenKey = $"{prefix}_Auth_Token";
            _playerIdKey = $"{prefix}_Auth_PlayerId";
            _expiryKey = $"{prefix}_Auth_Expiry";
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

            var cached = new CachedToken(token, playerId, expiry);
            return cached.IsValid ? cached : null;
        }

        public void Save(CachedToken token)
        {
            PlayerPrefs.SetString(_tokenKey, token.Token);
            PlayerPrefs.SetString(_playerIdKey, token.PlayerId);
            PlayerPrefs.SetString(_expiryKey, token.ExpiresAt.ToString("O", CultureInfo.InvariantCulture));
            PlayerPrefs.Save();
        }

        public void Clear()
        {
            PlayerPrefs.DeleteKey(_tokenKey);
            PlayerPrefs.DeleteKey(_playerIdKey);
            PlayerPrefs.DeleteKey(_expiryKey);
            PlayerPrefs.Save();
        }
    }
}
#endif
