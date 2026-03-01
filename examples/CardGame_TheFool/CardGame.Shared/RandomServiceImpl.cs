namespace CardGame.Shared
{
    /// <summary>
    /// Real implementation of IRandomService.
    /// Used on server side, wrapped by generated Recorder.
    /// </summary>
    public class RandomServiceImpl : IRandomService
    {
        private readonly Random _random = new();

        public int Next(int max)
        {
            var result = _random.Next(max);
            Console.WriteLine($"   [Random] Generated {result} (max: {max})");
            return result;
        }
    }
}
