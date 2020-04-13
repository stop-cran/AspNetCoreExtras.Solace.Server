using SolaceSystems.Solclient.Messaging;

namespace AspNetCoreExtras.Solace.Server
{
    public interface ISolaceFeature
    {
        string RequestApplicationMessageType { get; }
        string? ResponseApplicationMessageType { get; set; }
        IDestination RequestDestination { get; }
        IDestination? ResponseDestination { get; }
    }
}
