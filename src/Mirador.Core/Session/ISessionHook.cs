using Mirador.Core.Modules;
using Mirador.Core.Profiles;

namespace Mirador.Core.Session;

public interface ISessionHook
{
    void Started(Profile profile, IHeadsetModule? module);

    void Ended(Profile profile, IHeadsetModule? module);
}
