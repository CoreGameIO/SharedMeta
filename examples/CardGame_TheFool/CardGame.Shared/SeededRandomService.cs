namespace CardGame.Shared
{
    /// <summary>
    /// Seeded implementation of IRandomService for deterministic game simulation.
    /// Call Reset(seed) before each game to get reproducible shuffles.
    /// </summary>
    public class SeededRandomService : IRandomService
    {
        private Random _random;

        public SeededRandomService(int seed)
        {
            _random = new Random(seed);
        }

        public void Reset(int seed)
        {
            _random = new Random(seed);
        }

        public int Next(int max)
        {
            return _random.Next(max);
        }
    }
}
