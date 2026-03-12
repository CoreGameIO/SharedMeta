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
        private const string TokenKey = "SharedMeta_Auth_Token";
        private const string PlayerIdKey = "SharedMeta_Auth_PlayerId";
        private const string ExpiryKey = "SharedMeta_Auth_Expiry";

        public CachedToken? Load()
        {
            if (!PlayerPrefs.HasKey(TokenKey))
                return null;

            var token = PlayerPrefs.GetString(TokenKey);
            var playerId = PlayerPrefs.GetString(PlayerIdKey, "");
            var expiryStr = PlayerPrefs.GetString(ExpiryKey, "");

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
            PlayerPrefs.SetString(TokenKey, token.Token);
            PlayerPrefs.SetString(PlayerIdKey, token.PlayerId);
            PlayerPrefs.SetString(ExpiryKey, token.ExpiresAt.ToString("O", CultureInfo.InvariantCulture));
            PlayerPrefs.Save();
        }

        public void Clear()
        {
            PlayerPrefs.DeleteKey(TokenKey);
            PlayerPrefs.DeleteKey(PlayerIdKey);
            PlayerPrefs.DeleteKey(ExpiryKey);
            PlayerPrefs.Save();
        }
    }
}
#endif
