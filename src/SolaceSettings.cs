using SolaceSystems.Solclient.Messaging;
using System;

namespace AspNetCoreExtras.Solace.Server
{
    public class SolaceServerOptions
    {
        public SessionProperties SessionProperties { get; set; } = new SessionProperties();
        public string[] Topics { get; set; } = Array.Empty<string>();
        public SolLogLevel SolClientLogLevel { get; set; } = SolLogLevel.Warning;
    }
}
