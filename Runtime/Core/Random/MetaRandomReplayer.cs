namespace SharedMeta.Core.Random
{
    /// <summary>
    /// Client-side replayer for ServerRandom.
    /// Reads pre-recorded values from replay payload instead of generating them.
    /// </summary>
    public class MetaRandomReplayer : IMetaRandom
    {
        private readonly IClientReplayContext _context;
        private long _scrollId;

        public long ScrollId => _scrollId;

        public MetaRandomReplayer(IClientReplayContext context)
        {
            _context = context;
        }

        public int Next(int max)
        {
            _scrollId++;
            return _context.Reader.Read<int>();
        }

        public int Next(int min, int max)
        {
            _scrollId++;
            return _context.Reader.Read<int>();
        }

        public float NextFloat()
        {
            _scrollId++;
            return _context.Reader.Read<float>();
        }
    }
}
