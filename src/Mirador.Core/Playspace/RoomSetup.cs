using Mirador.Core.Lighthouse;
using Mirador.Core.Monado.Libmonado;

namespace Mirador.Core.Playspace;

public sealed record RoomSetup(string OriginName, string? Universe, MonadoPose Offset, DateTimeOffset Taken, SteamVrBase? Base = null);
