using MemoryPack;
using Orleans;

namespace CardGame.Shared
{
    [GenerateSerializer]
    public enum Suit
    {
        Hearts,
        Diamonds,
        Clubs,
        Spades
    }

    [GenerateSerializer]
    public enum Rank
    {
        Six = 6,
        Seven = 7,
        Eight = 8,
        Nine = 9,
        Ten = 10,
        Jack = 11,
        Queen = 12,
        King = 13,
        Ace = 14
    }

    [MemoryPackable, GenerateSerializer]
    public partial class Card
    {
        [MemoryPackOrder(0), Id(0)] public Suit Suit { get; set; }
        [MemoryPackOrder(1), Id(1)] public Rank Rank { get; set; }

        public override string ToString() => $"{Rank} of {Suit}";

        public override bool Equals(object? obj)
        {
            if (obj is Card other)
                return Suit == other.Suit && Rank == other.Rank;
            return false;
        }

        public override int GetHashCode() => HashCode.Combine(Suit, Rank);

        /// <summary>
        /// Returns true if <paramref name="defense"/> beats <paramref name="attack"/> under the given trump suit.
        /// </summary>
        public static bool Beats(Card defense, Card attack, Suit trumpSuit)
        {
            if (defense.Suit == attack.Suit && defense.Rank > attack.Rank)
                return true;
            if (defense.Suit == trumpSuit && attack.Suit != trumpSuit)
                return true;
            return false;
        }
    }
}
