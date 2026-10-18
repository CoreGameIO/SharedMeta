#if UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using SharedMeta.Core.Transport;
using SharedMeta.Core;
using UnityEngine;

#nullable enable

namespace SharedMeta.Client
{
    /// <summary>
    /// 0.24.0+ Unity implementation of <see cref="IServerAnnotationCache"/> persisting entries
    /// to <see cref="PlayerPrefs"/> so the cache survives app launches. A freshly-installed
    /// app pays the phase-2 cost once per <c>(clientHash × serverHash)</c> pair; subsequent
    /// launches hit the cache and skip phase-2 entirely on connect.
    /// <para>
    /// Storage format: one PlayerPrefs entry per cached annotation, key
    /// <c>{prefix}_SigAnn_{clientHashHex}</c>, value = MemoryPack-encoded
    /// <see cref="ClientSignatureAnnotated"/> as base64. Prefix isolates by
    /// <see cref="Application.identifier"/> so multiple apps on the same device cannot collide.
    /// </para>
    /// <para>
    /// Backed by a lazy in-memory dictionary so repeated lookups within a session don't
    /// re-decode the base64 payload — only the first <see cref="TryGet"/> per clientHash
    /// touches PlayerPrefs.
    /// </para>
    /// </summary>
    public class PlayerPrefsServerAnnotationCache : IServerAnnotationCache
    {
        private readonly string _keyPrefix;
        private readonly IMetaSerializer _serializer;
        private readonly Dictionary<ulong, ClientSignatureAnnotated> _memory = new();
        private readonly object _gate = new();

        /// <summary>
        /// Construct a cache rooted at the supplied serializer's wire form, isolated by
        /// <see cref="Application.identifier"/>. The serializer should match the one used for
        /// network transport so the bytes round-trip identically on every platform.
        /// </summary>
        public PlayerPrefsServerAnnotationCache(IMetaSerializer serializer)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _keyPrefix = $"{Application.identifier}_SigAnn_";
        }

        public ClientSignatureAnnotated? TryGet(ulong clientSignatureHash)
        {
            lock (_gate)
            {
                if (_memory.TryGetValue(clientSignatureHash, out var cached)) return cached;

                var key = KeyFor(clientSignatureHash);
                if (!PlayerPrefs.HasKey(key)) return null;

                var base64 = PlayerPrefs.GetString(key);
                if (string.IsNullOrEmpty(base64)) return null;

                try
                {
                    var bytes = Convert.FromBase64String(base64);
                    var annotated = _serializer.Unpack<ClientSignatureAnnotated>(bytes);
                    if (annotated != null) _memory[clientSignatureHash] = annotated;
                    return annotated;
                }
                catch (Exception ex)
                {
                    // Corrupt or schema-shifted entry — drop it so phase-2 fetches fresh. Don't
                    // throw on the connect path; a bad PlayerPrefs entry shouldn't block login.
                    Debug.LogWarning($"PlayerPrefsServerAnnotationCache: failed to decode entry for {clientSignatureHash:X16}, dropping ({ex.Message})");
                    PlayerPrefs.DeleteKey(key);
                    return null;
                }
            }
        }

        public void Set(ClientSignatureAnnotated annotated)
        {
            if (annotated == null) return;
            lock (_gate)
            {
                _memory[annotated.ClientSignatureHash] = annotated;
                var bytes = _serializer.Pack(annotated);
                // Pack returns ROM over per-grain scratch on server; on the client path this is a
                // direct byte[] allocation. ToArray() copies out before base64 to be safe regardless.
                var arr = bytes.IsEmpty ? Array.Empty<byte>() : bytes.ToArray();
                PlayerPrefs.SetString(KeyFor(annotated.ClientSignatureHash), Convert.ToBase64String(arr));
                PlayerPrefs.Save();
            }
        }

        public void Invalidate(ulong clientSignatureHash)
        {
            lock (_gate)
            {
                _memory.Remove(clientSignatureHash);
                PlayerPrefs.DeleteKey(KeyFor(clientSignatureHash));
                PlayerPrefs.Save();
            }
        }

        private string KeyFor(ulong clientSignatureHash) => _keyPrefix + clientSignatureHash.ToString("X16");
    }
}
#endif
