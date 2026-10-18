using System;

namespace SharedMeta.Core.Random
{
    /// <summary>
    /// Server-side wrapper for MetaRandom that records results to replay payload.
    /// Used for ServerRandom: server generates real values, client reads them from replay.
    /// </summary>
    public class MetaRandomRecorder : IMetaRandom
    {
        private readonly MetaRandom _real;
        private readonly IServerRecordContext _context;

        public long ScrollId => _real.ScrollId;

        public MetaRandomRecorder(MetaRandom real, IServerRecordContext context)
        {
            _real = real ?? throw new ArgumentNullException(nameof(real));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public int Next(int max)
        {
            var result = _real.Next(max);
            _context.Writer.Write(result);
            if (_context.DebugEnabled) _context.WriteDebugInfo($"ServerRandom.Next({max}) -> {result}");
            return result;
        }

        public int Next(int min, int max)
        {
            var result = _real.Next(min, max);
            _context.Writer.Write(result);
            if (_context.DebugEnabled) _context.WriteDebugInfo($"ServerRandom.Next({min}, {max}) -> {result}");
            return result;
        }

        public float NextFloat()
        {
            var result = _real.NextFloat();
            _context.Writer.Write(result);
            if (_context.DebugEnabled) _context.WriteDebugInfo($"ServerRandom.NextFloat() -> {result}");
            return result;
        }
    }
}
