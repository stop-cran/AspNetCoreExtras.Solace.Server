using Microsoft.Extensions.Hosting;
using System;

namespace AspNetCoreExtras.Solace.Server
{
    public interface IObservableSessionService : IObservableSession, IHostedService, IDisposable { }
}