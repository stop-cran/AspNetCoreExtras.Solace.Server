using SolaceSystems.Solclient.Messaging;

namespace AspNetCoreExtras.Solace.Server
{
    public class SolaceFeature : ISolaceFeature
    {
        public SolaceFeature(IMessage requestMessage) : this(
            requestMessage.ApplicationMessageType,
            requestMessage.Destination,
            requestMessage.ReplyTo,
            requestMessage.CorrelationId)
        { }

        public SolaceFeature(
            string requestApplicationMessageType,
            IDestination requestDestination,
            IDestination? responseDestination,
            string? correlationId)
        {
            RequestApplicationMessageType = requestApplicationMessageType;
            RequestDestination = requestDestination;
            ResponseDestination = responseDestination;
            CorrelationId = correlationId;
        }

        public string RequestApplicationMessageType { get; }
        public string? ResponseApplicationMessageType { get; set; }
        public IDestination RequestDestination { get; }
        public IDestination? ResponseDestination { get; }
        public string? CorrelationId { get; }
    }
}
