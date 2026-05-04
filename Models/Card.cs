namespace PlayCards.Models;

public enum Suit
{
    Clubs,
    Diamonds,
    Hearts,
    Spades
}

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

public sealed record Card(Suit Suit, Rank Rank)
{
    public string Code => $"{RankToCode(Rank)}{SuitToCode(Suit)}";
    public string Label => $"{RankToLabel(Rank)}{SuitToSymbol(Suit)}";
    public bool IsRed => Suit is Suit.Diamonds or Suit.Hearts;

    public static string RankToCode(Rank rank) => rank switch
    {
        Rank.Jack => "J",
        Rank.Queen => "Q",
        Rank.King => "K",
        Rank.Ace => "A",
        _ => ((int)rank).ToString()
    };

    public static string RankToLabel(Rank rank) => rank switch
    {
        Rank.Jack => "В",
        Rank.Queen => "Д",
        Rank.King => "К",
        Rank.Ace => "Т",
        _ => ((int)rank).ToString()
    };

    public static string SuitToCode(Suit suit) => suit switch
    {
        Suit.Clubs => "C",
        Suit.Diamonds => "D",
        Suit.Hearts => "H",
        Suit.Spades => "S",
        _ => "?"
    };

    public static string SuitToSymbol(Suit suit) => suit switch
    {
        Suit.Clubs => "♣",
        Suit.Diamonds => "♦",
        Suit.Hearts => "♥",
        Suit.Spades => "♠",
        _ => "?"
    };
}
