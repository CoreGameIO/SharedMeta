using System.Collections.Generic;
using System.Diagnostics;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>
    /// Recording side of the opt-in transport metrics (<see cref="MetaTelemetryOptions.TransportMetrics"/>),
    /// shared by the SignalR hub filters and the HTTP endpoint filters so every transport emits
    /// the same instruments with the same tag values.
    /// </summary>
    public static class TransportTelemetry
    {
        public const string SignalR = "signalr";
        public const string HttpPolling = "http_polling";
        public const string Mux = "mux";
        public const string Http = "http";

        public const string Success = "success";
        public const string Error = "error";
        public const string Unauthorized = "unauthorized";
        public const string BadRequest = "bad_request";

        /// <summary>Records one hub invocation / HTTP request.</summary>
        /// <param name="recordDuration">False for long-poll style endpoints whose duration is the poll timeout.</param>
        public static void Record(string transport, string endpoint, string result, long startTimestamp, bool recordDuration = true)
        {
            var tags = new TagList
            {
                { "transport", transport },
                { "endpoint", endpoint },
                { "result", result },
            };
            SharedMetaMeters.TransportRequests.Add(1, tags);
            if (recordDuration)
                SharedMetaMeters.TransportRequestDuration.Record(
                    Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, tags);
        }

        /// <summary>Maps an HTTP status code to the <c>result</c> tag.</summary>
        public static string ResultOf(int statusCode) => statusCode switch
        {
            401 or 403 => Unauthorized,
            >= 400 and < 500 => BadRequest,
            >= 500 => Error,
            _ => Success,
        };

        public static void RecordBatch(string endpoint, int size)
            => SharedMetaMeters.TransportBatchSize.Record(size,
                new KeyValuePair<string, object?>("endpoint", endpoint));

        /// <summary>One auth endpoint request; <paramref name="provider"/> is <c>none</c> when not applicable.</summary>
        public static void RecordAuth(string endpoint, string? provider, string result)
            => SharedMetaMeters.AuthRequests.Add(1,
                new KeyValuePair<string, object?>("endpoint", endpoint),
                new KeyValuePair<string, object?>("provider", NormalizeProvider(provider)),
                new KeyValuePair<string, object?>("result", result));

        // Providers arrive from request bodies — keep the tag bounded to a known short token.
        private static string NormalizeProvider(string? provider)
        {
            if (string.IsNullOrEmpty(provider)) return "none";
            var p = provider!.Trim().ToLowerInvariant();
            if (p.Length > 24) return "other";
            foreach (var c in p)
                if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '_' || c == '-'))
                    return "other";
            return p;
        }
    }
}
