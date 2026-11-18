using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Playspace;

/// <summary>A room setup Mirador keeps for one tracking origin, in <c>roomsetup.json</c>.</summary>
/// <param name="OriginName">The tracking origin's name, as libmonado gives it.</param>
/// <param name="Universe">The Lighthouse universe it was taken in; null when there was none.</param>
/// <param name="Offset">
/// The origin's offset to set after each Monado start, as it is (Monado forgets its offsets when it stops).
/// </param>
/// <param name="Taken">When it was measured.</param>
public sealed record RoomSetup(string OriginName, string? Universe, MonadoPose Offset, DateTimeOffset Taken);
