using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolaceSystems.Solclient.Messaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCoreExtras.Solace.Server
{
    public class SolaceServer : IServer, ISolaceServer
    {
        private readonly SolaceSettings solaceSettings;
        private readonly IContext context;
        private readonly IReadOnlyList<ITopic> topics;
        private readonly ILogger<SolaceServer> logger;

        private readonly BlockingCollection<IMessage> messages =
            new BlockingCollection<IMessage>();

        private readonly CancellationTokenSource messageProcessingCancellation =
            new CancellationTokenSource();

        public SolaceServer(IContext context, IOptions<SolaceServerOptions> options, ILogger<SolaceServer> logger)
        {
            this.context = context;
            this.logger = logger;
            solaceSettings = options.Value.Solace;

            var addressFeature = new ServerAddressesFeature();

            foreach (var topic in options.Value.Solace.Topics)
                addressFeature.Addresses.Add(
                    solaceSettings.SessionProperties.Host + ':' +
                    solaceSettings.SessionProperties.VPNName + ':'
                    + topic);

            Features.Set<IHttpRequestFeature>(new HttpRequestFeature());
            Features.Set<IHttpResponseFeature>(new HttpResponseFeature());
            Features.Set<IServerAddressesFeature>(addressFeature);
            Features.Set<IRoutingFeature>(new RoutingFeature
            {
                RouteData = new RouteData()
            });

            topics = options.Value.Solace.Topics
                .Select(ContextFactory.Instance.CreateTopic)
                .ToList()
                .AsReadOnly();
        }

        public SolaceSystems.Solclient.Messaging.ISession? Session { get; private set; }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public event EventHandler? Connected;
        public event EventHandler? Disconnected;

        private void OnSessionEvent(SessionEventArgs args)
        {
            switch (args.Event)
            {
                case SessionEvent.UpNotice:
                case SessionEvent.Reconnected:
                    Connected?.Invoke(this, EventArgs.Empty);
                    break;

                case SessionEvent.Reconnecting:
                case SessionEvent.DownError:
                case SessionEvent.ConnectFailedError:
                    Disconnected?.Invoke(this, EventArgs.Empty);
                    break;
            }

            using (logger.BeginScope(new
            {
                @event = args.Event,
                code = args.ResponseCode,
                info = args.Info
            }))
                logger.Log(ToLogLevel(args.Event), "Solace event.");
        }

        private static LogLevel ToLogLevel(SessionEvent sessionEvent)
        {
            switch (sessionEvent)
            {
                case SessionEvent.ProvisionOk:
                case SessionEvent.SubscriptionOk:
                    return LogLevel.Debug;

                case SessionEvent.UpNotice:
                case SessionEvent.Reconnecting:
                case SessionEvent.Reconnected:
                case SessionEvent.RepublishUnackedMessages:
                    return LogLevel.Information;

                case SessionEvent.DownError:
                case SessionEvent.ConnectFailedError:
                case SessionEvent.VirtualRouterNameChanged:
                    return LogLevel.Warning;

                default:
                    return LogLevel.Error;
            }
        }

        private Func<object, HttpContext>? contextConverter;
        private Task? messageProcessingTask;

        public async Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            contextConverter = GetHttpContextConverter<TContext>();

            Session = context.CreateSession(solaceSettings.SessionProperties,
                    (sender, e) => messages.Add(e.Message), (sender, e) => OnSessionEvent(e));

            var connectReturnCode = await Task.Run(Session.Connect);

            if (connectReturnCode != ReturnCode.SOLCLIENT_OK)
                using (logger.BeginScope(new
                {
                    host = solaceSettings.SessionProperties.Host,
                    vpn = solaceSettings.SessionProperties.VPNName,
                    userName = solaceSettings.SessionProperties.UserName,
                    code = connectReturnCode
                }))
                    logger.LogError("Error connecting Solace.");

            foreach (var topic in topics)
            {
                var subscribeReturnCode = await Task.Run(() => Session.Subscribe(topic, true));

                if (subscribeReturnCode != ReturnCode.SOLCLIENT_OK)
                    using (logger.BeginScope(new
                    {
                        host = solaceSettings.SessionProperties.Host,
                        vpn = solaceSettings.SessionProperties.VPNName,
                        topic,
                        code = subscribeReturnCode
                    }))
                        logger.LogError("Error subscribing Solace topic.");
            }

            messageProcessingTask = Task.WhenAll(Enumerable.Range(0, solaceSettings.MaxParallelRequests)
                .Select(_ => ProcessMessages(application, messageProcessingCancellation.Token))
                .ToArray());
        }

        private async Task ProcessMessages<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        {
            await Task.Yield();

            foreach (var message in messages.GetConsumingEnumerable(cancellationToken))
                try
                {
                    var context = application.CreateContext(Features)!;
                    var httpContext = contextConverter!(context);
                    using var responseStream = new MemoryStream();

                    FillRequest(httpContext.Request, message);

                    httpContext.Response.ContentType = httpContext.Request.ContentType;
                    httpContext.Response.Body = responseStream;

                    await application.ProcessRequestAsync(context);

                    using var responseMessage = Session!.CreateMessage();

                    FillResponse(httpContext.Response, responseMessage);

                    var sendReplyReturnCode = Session.SendReply(message, responseMessage);

                    if (sendReplyReturnCode != ReturnCode.SOLCLIENT_OK)
                        using (logger.BeginScope(new
                        {
                            host = solaceSettings.SessionProperties.Host,
                            vpn = solaceSettings.SessionProperties.VPNName,
                            code = sendReplyReturnCode
                        }))
                            logger.LogError("Error sending response.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected error processing message.");
                }
        }

        private static Func<object, HttpContext> GetHttpContextConverter<TContext>()
        {
            var parameter = Expression.Parameter(typeof(object));

            return Expression.Lambda<Func<object, HttpContext>>(
                Expression.Property(
                    Expression.Convert(parameter, typeof(TContext)),
                    typeof(TContext).GetProperty("HttpContext")),
                parameter).Compile();
        }

        protected virtual void FillRequest(HttpRequest request, IMessage requestMessage)
        {
            request.Method = HttpMethods.Post;
            request.Path = new PathString('/' + requestMessage.ApplicationMessageType);
            request.ContentLength = requestMessage.BinaryAttachment.Length;
            request.ContentType = solaceSettings.ContentType;

            request.Headers["Destination"] = requestMessage.Destination.Name;
            request.Headers["ReplyTo"] = requestMessage.ReplyTo.Name;
            request.Headers["ApplicationMessageType"] = requestMessage.ApplicationMessageType;

            request.Body = new MemoryStream(requestMessage.BinaryAttachment);
        }

        protected virtual void FillResponse(HttpResponse response, IMessage responseMessage)
        {
            responseMessage.ApplicationMessageType = response.Headers["ApplicationMessageType"];
            responseMessage.BinaryAttachment = ReadAllBytes(response.Body);
        }

        private static byte[] ReadAllBytes(Stream stream)
        {
            if (stream is MemoryStream ms)
                return ms.ToArray();

            using var memoryStream = new MemoryStream();

            stream.CopyTo(memoryStream);

            return memoryStream.ToArray();
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            messageProcessingCancellation.Cancel();

            try
            {
                await messageProcessingTask!;
            }
            catch (OperationCanceledException)
            { }

            Session!.Disconnect();
        }

        public virtual void Dispose()
        {
            messageProcessingCancellation.Dispose();
            messages.Dispose();
            Session?.Dispose();

            foreach (var topic in topics)
                topic.Dispose();
        }
    }
}
