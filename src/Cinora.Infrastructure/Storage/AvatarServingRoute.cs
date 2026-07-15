namespace Cinora.Infrastructure.Storage;

/// <summary>
/// The single source of truth for the avatar-serving route base segment (ADR 0013 §2.4). The <b>mint</b> side
/// (<see cref="LocalFileStorage.GetUrl"/>, via <c>FileStorageOptions.PublicBasePath</c>) and the <b>serve</b>
/// side (Web's <c>MediaController</c> <c>[Route]</c>) must target the same path, but they live in different
/// layers and would otherwise agree only by convention — an operator changing <c>PublicBasePath</c> would then
/// silently point every minted avatar URL at a route the controller does not serve (all avatars 404, no boot
/// error). Both sides reference this const, and <c>FileStorageRouteValidateOptions</c> asserts they still line up
/// at boot, so the coupling is explicit and cannot silently drift.
/// </summary>
public static class AvatarServingRoute
{
    /// <summary>
    /// The route base segment (no leading slash) the media controller serves avatars under: <c>uploads</c>.
    /// Referenced by the <c>MediaController</c> <c>[Route]</c> attribute (which requires a compile-time constant).
    /// </summary>
    public const string RouteBase = "uploads";

    /// <summary>
    /// The public URL base path a minted avatar URL must target — <c>/uploads</c> — i.e. a leading slash plus
    /// <see cref="RouteBase"/>. Used as the default for <c>FileStorageOptions.PublicBasePath</c> and by the
    /// boot-time route-consistency validator.
    /// </summary>
    public const string PublicBasePath = "/" + RouteBase;
}
