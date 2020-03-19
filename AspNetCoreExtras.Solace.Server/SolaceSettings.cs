﻿using SolaceSystems.Solclient.Messaging;
using System;

namespace AspNetCoreExtras.Solace.Server
{
    public class SolaceSettings
    {
        public SessionProperties SessionProperties { get; set; } = new SessionProperties();
        public string[] Topics { get; set; } = Array.Empty<string>();
        public string ContentType { get; set; }
        public int MaxParallelRequests { get; set; }
    }
}
