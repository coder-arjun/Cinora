using Cinora.Domain.Exceptions;

namespace Cinora.Domain.ValueObjects;

/// <summary>
/// A movie/series score on Cinora's 1–10 scale. An immutable value object with value equality:
/// two ratings are equal when their <see cref="Value"/> is equal. All valid ratings must be created
/// through <see cref="From(int)"/>, which enforces the 1–10 invariant. Because this is a readonly
/// record struct, the struct default (<c>default(Rating)</c> or <c>new Rating()</c>) bypasses
/// <see cref="From(int)"/> and holds <see cref="Value"/> <c>0</c>, which is NOT a valid rating;
/// consumers must reject it via <see cref="IsValid"/>.
/// </summary>
public readonly record struct Rating
{
    /// <summary>The lowest score a rating may hold.</summary>
    public const int MinValue = 1;

    /// <summary>The highest score a rating may hold.</summary>
    public const int MaxValue = 10;

    private Rating(int value) => Value = value;

    /// <summary>The score, guaranteed to be between <see cref="MinValue"/> and <see cref="MaxValue"/> inclusive.</summary>
    public int Value { get; }

    /// <summary>
    /// Whether this rating holds a score within the valid <see cref="MinValue"/>–<see cref="MaxValue"/>
    /// range. Returns <see langword="false"/> for the struct default (<c>default(Rating)</c> /
    /// <c>new Rating()</c>), which bypasses <see cref="From(int)"/>.
    /// </summary>
    public bool IsValid => Value is >= MinValue and <= MaxValue;

    /// <summary>Creates a <see cref="Rating"/> from a raw score.</summary>
    /// <param name="value">The score; must be between 1 and 10 inclusive.</param>
    /// <returns>A valid <see cref="Rating"/>.</returns>
    /// <exception cref="DomainException">Thrown when <paramref name="value"/> is outside 1–10.</exception>
    public static Rating From(int value)
    {
        if (value is < MinValue or > MaxValue)
        {
            throw new DomainException("Rating must be between 1 and 10.");
        }

        return new Rating(value);
    }
}
