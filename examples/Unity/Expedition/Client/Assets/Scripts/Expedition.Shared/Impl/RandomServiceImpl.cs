using System;

namespace Expedition.Shared
{
    public class RandomServiceImpl : IRandomService
    {
        private readonly Random _random = new Random();

        public int Next(int max)
        {
            return _random.Next(max);
        }
    }
}
