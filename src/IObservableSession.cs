using SolaceSystems.Solclient.Messaging;
using System;

namespace AspNetCoreExtras.Solace.Server
{
    public interface IObservableSession
    {
        ISession? Session { get; }
        IObservable<IMessage> Messages { get; }
        IObservable<SessionEventArgs> SessionEvents { get; }
    }
}
