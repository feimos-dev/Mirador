using Mirador.Ipc;

namespace Mirador.Helper.ElevatedMonado;

public interface IElevatedMonado
{
    HelperReply Start(ElevatedStart start, CallerInfo caller);

    HelperReply Stop(CallerInfo caller, bool clean);

    HelperReply Wait(CallerInfo caller);
}
