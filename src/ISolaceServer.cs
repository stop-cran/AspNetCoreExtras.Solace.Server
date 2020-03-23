using SolaceSystems.Solclient.Messaging;
using System;

namespace AspNetCoreExtras.Solace.Server
{
    public interface ISolaceServer
    {
        ISession? Session { get; }

        event EventHandler Connected;
        event EventHandler Disconnected;
    }
}