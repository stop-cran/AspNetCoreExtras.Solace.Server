using SolaceSystems.Solclient.Messaging;

namespace AspNetCoreExtras.Solace.Server
{
    public class SolaceFeature : ISolaceFeature
    {
        public SolaceFeature(IMessage requestMessage) : this(
            requestMessage.ApplicationMessageType,
            requestMessage.Destination,
            requestMessage.ReplyTo)
        { }

        public SolaceFeature(
            string requestApplicationMessageType,
            IDestination requestDestination,
            IDestination responseDestination)
        {
            RequestApplicationMessageType = requestApplicationMessageType;
            RequestDestination = requestDestination;
            ResponseDestination = responseDestination;
        }

        public string RequestApplicationMessageType { get; }
        public string ResponseApplicationMessageType { get; set; } = "";
        public IDestination RequestDestination { get; }
        public IDestination ResponseDestination { get; }
        public bool IsOneWay { get; set; }
    }
}
