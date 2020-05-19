using SolaceSystems.Solclient.Messaging;
using System.Collections.Generic;

namespace AspNetCoreExtras.Solace.Server
{
    public class SolaceServerOptions
    {
        public List<string> Topics { get; set; } = new List<string>();
        public SolLogLevel SolClientLogLevel { get; set; } = SolLogLevel.Warning;
    }
}
