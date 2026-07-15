namespace Cinora.Domain.Enums;

/// <summary>The kind of catalogued title a <see cref="Entities.Movie"/> represents.</summary>
public enum MediaType
{
    /// <summary>A feature film.</summary>
    Movie = 0,

    /// <summary>A multi-episode television or streaming series.</summary>
    Series = 1,
}
