namespace SharedMeta.Core.Patch
{
    /// <summary>
    /// Interface for generated PatchWrapper classes.
    /// Provides access to the underlying PatchNode for CRC computation in deep desync detection.
    /// </summary>
    public interface IPatchWrapper
    {
        /// <summary>The underlying PatchNode tree. Null when tracking is not active.</summary>
        PatchNode? Node { get; }

        /// <summary>Whether patch tracking is currently active.</summary>
        bool IsTracking { get; }
    }
}
