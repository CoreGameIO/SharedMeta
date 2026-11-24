using System;
using SharedMeta.Core.Transport;
#nullable enable

namespace SharedMeta.Client
{
    /// <summary>
    /// Thrown by <see cref="MetaClient.ConnectAsync"/> when the server refuses the session.
    /// <see cref="FailureReason"/> carries the server's structured verdict so callers can route
    /// recovery without parsing the message — most importantly
    /// <see cref="SessionConnectFailureReason.IdentityUnknown"/>: the cached token outlived its
    /// account, and only discarding it and logging in again (which yields a new PlayerId) helps.
    /// Derives from <see cref="InvalidOperationException"/>, the type this failure was thrown as
    /// before, so existing catch blocks keep working.
    /// </summary>
    public sealed class MetaSessionConnectException : InvalidOperationException
    {
        /// <summary>Why the server refused the session.</summary>
        public SessionConnectFailureReason FailureReason { get; }

        /// <summary>The server's error text, without the client-side prefix.</summary>
        public string? ServerError { get; }

        public MetaSessionConnectException(SessionConnectFailureReason failureReason, string? serverError)
            : base("Failed to establish session: " + serverError)
        {
            FailureReason = failureReason;
            ServerError = serverError;
        }
    }
}
