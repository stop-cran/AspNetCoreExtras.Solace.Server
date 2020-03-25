using SolaceSystems.Solclient.Messaging;
using System;

namespace AspNetCoreExtras.Solace.Server
{
    public class SolaceSettings
    {
        public SessionProperties SessionProperties { get; set; } = new SessionProperties();
        public string[] Topics { get; set; } = Array.Empty<string>();
        public int MaxParallelRequests { get; set; } = 1;
        public SolLogLevel SolClientLogLevel { get; set; } = SolLogLevel.Warning;
    }
}
