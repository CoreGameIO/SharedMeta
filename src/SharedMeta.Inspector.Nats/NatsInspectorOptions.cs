using NATS.Client.Core;

namespace SharedMeta.Inspector.Nats;

/// <summary>How the inspector of this process uses NATS JetStream.</summary>
public sealed class NatsInspectorOptions
{
    /// <summary>
    /// Server URL(s), comma-separated. 127.0.0.1 rather than localhost: on hosts where a published
    /// port does not answer on ::1 (WSL mirrored networking) localhost would hang first.
    /// </summary>
    public string Url { get; set; } = "nats://127.0.0.1:4222";

    /// <summary>Last say over the connection (credentials, TLS, …).</summary>
    public Func<NatsOpts, NatsOpts>? ConfigureConnection { get; set; }

    /// <summary>JetStream stream holding the events. One per cluster (or per environment).</summary>
    public string StreamName { get; set; } = "SHAREDMETA_INSPECTOR";

    /// <summary>Subject prefix; events go to <c>{prefix}.ev.{stateType}.{entityId}</c>.</summary>
    public string SubjectPrefix { get; set; } = "sharedmeta.inspector";

    /// <summary>Key-value bucket with the shared capture settings.</summary>
    public string ControlBucket { get; set; } = "sharedmeta-inspector";

    /// <summary>Create / update the stream and bucket on start. Off when operations manage them.</summary>
    public bool ManageStream { get; set; } = true;

    /// <summary>Stream retention: how long events stay readable as history.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Stream retention by size; the oldest events go first.</summary>
    public long MaxBytes { get; set; } = 1L << 30;

    public int Replicas { get; set; } = 1;

    /// <summary>Send this process's captured events to the stream. Off for a standalone inspector.</summary>
    public bool Publish { get; set; } = true;

    /// <summary>
    /// Feed this process's store from the stream (every silo's events). Off on silos that do not
    /// serve the inspector tools: they only publish.
    /// </summary>
    public bool Consume { get; set; } = true;

    /// <summary>On start, load events this far back into the store (zero: only new events).</summary>
    public TimeSpan WarmStart { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Read entity timelines beyond the in-memory window back from the stream.</summary>
    public bool History { get; set; } = true;

    /// <summary>Share capture settings (levels, watches) with every process on the bucket.</summary>
    public bool ShareCaptureSettings { get; set; } = true;
}
