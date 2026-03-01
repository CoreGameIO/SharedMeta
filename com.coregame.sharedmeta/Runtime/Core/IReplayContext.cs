namespace SharedMeta.Core
{
    /// <summary>
    /// Interface for replay context operations.
    /// Allows clients to begin/end replay without reflection.
    /// Implemented by ClientMetaContext.
    /// </summary>
    public interface IReplayContext
    {
        /// <summary>
        /// Begin replay mode with recorded payload.
        /// Sets the context to read recorded values during method execution.
        /// </summary>
        /// <param name="replayPayload">The recorded payload bytes from server.</param>
        void BeginReplay(byte[] replayPayload);

        /// <summary>
        /// End replay mode.
        /// Resets the context to normal operation.
        /// </summary>
        void EndReplay();
    }
}
