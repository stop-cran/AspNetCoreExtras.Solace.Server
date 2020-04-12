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
        private readonly SolaceServerOptions options;
        private readonly IContext context;
        private readonly IReadOnlyList<ITopic> topics;
        private readonly ILogger<SolaceServer> logger;

        private readonly BlockingCollection<object> contexts =
            new BlockingCollection<object>();

        private readonly CancellationTokenSource messageProcessingCancellation =
            new CancellationTokenSource();

        public SolaceServer(IContext context, IOptions<SolaceServerOptions> options, ILogger<SolaceServer> logger)
        {
            this.context = context;
            this.logger = logger;
            this.options = options.Value;

            var addressFeature = new ServerAddressesFeature();

            foreach (var topic in options.Value.Topics)
                addressFeature.Addresses.Add(
                    this.options.SessionProperties.Host + ':' +
                    this.options.SessionProperties.VPNName + ':'
                    + topic);

            Features.Set<IHttpRequestFeature>(new HttpRequestFeature());
            Features.Set<IHttpResponseFeature>(new HttpResponseFeature());
            Features.Set<IServerAddressesFeature>(addressFeature);
            Features.Set<ISolaceFeature>(
                new SolaceFeature("", ContextFactory.Instance.CreateTopic(""), null, null));

            topics = this.options.Topics
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
                code = args.ResponseCode
            }))
                logger.Log(ToLogLevel(args.Event), args.Info);
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

        private Task? messageProcessingTask;

        public async Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var _ = logger.BeginScope(new
            {
                host = options.SessionProperties.Host,
                vpn = options.SessionProperties.VPNName
            });

            logger.LogDebug("The server is starting...");
            logger.LogDebug("Creating Solace session...");

            Session = context.CreateSession(options.SessionProperties,
                (sender, e) => EnqueueMessage(application, e.Message),
                (sender, e) => OnSessionEvent(e));

            logger.LogDebug("Connecting session...");

            var connectReturnCode = await Task.Run(Session.Connect);

            if (connectReturnCode != ReturnCode.SOLCLIENT_OK)
                using (logger.BeginScope(new
                {
                    userName = options.SessionProperties.UserName,
                    code = connectReturnCode
                }))
                    logger.LogError("Error connecting Solace.");

            logger.LogDebug("Subscribing...");

            foreach (var topic in topics)
            {
                var subscribeReturnCode = await Task.Run(() => Session.Subscribe(topic, true));

                if (subscribeReturnCode != ReturnCode.SOLCLIENT_OK)
                    using (logger.BeginScope(new
                    {
                        topic,
                        code = subscribeReturnCode
                    }))
                        logger.LogError("Error subscribing Solace topic.");
            }

            logger.LogDebug("Starting message processing loop...");

            messageProcessingTask = Task.WhenAll(Enumerable.Range(0, options.MaxParallelRequests)
                .Select(_ => ProcessMessages(application, messageProcessingCancellation.Token))
                .ToArray());

            logger.LogInformation("The server has been started...");
        }

        private void EnqueueMessage<TContext>(IHttpApplication<TContext> application, IMessage message)
        {
            try
            {
                var context = CreateContext(application, message);

                if (context != null)
                    contexts.Add(context);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error creating the app context.");
            }
        }

        private TContext CreateContext<TContext>(IHttpApplication<TContext> application, IMessage message)
        {
            var context = application.CreateContext(new FeatureCollection(Features))!;

            try
            {
                var httpContext = HttpApplicationContextHelper<TContext>.GetHttpContext(context);

                FillRequest(httpContext.Request, message);

                return context;
            }
            catch (Exception ex)
            {
                application.DisposeContext(context, ex);
                throw;
            }
        }

        private async Task ProcessMessages<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        {
            await Task.Yield();

            foreach (TContext context in contexts.GetConsumingEnumerable(cancellationToken))
                try
                {
                    var httpContext = HttpApplicationContextHelper<TContext>.GetHttpContext(context);
                    using var responseStream = new MemoryStream();

                    httpContext.Response.ContentType = httpContext.Request.ContentType;
                    httpContext.Response.Body = responseStream;

                    await application.ProcessRequestAsync(context);

                    var solaceFeature = httpContext.Features.Get<ISolaceFeature>();

                    if (solaceFeature.ResponseDestination != null)
                    {
                        using var responseMessage = Session!.CreateMessage();

                        responseMessage.Destination = solaceFeature.ResponseDestination;
                        responseMessage.CorrelationId = solaceFeature.CorrelationId;

                        FillResponse(httpContext.Response, responseMessage);

                        var sendReplyReturnCode = Session.Send(responseMessage);

                        if (sendReplyReturnCode != ReturnCode.SOLCLIENT_OK)
                            using (logger.BeginScope(new
                            {
                                host = options.SessionProperties.Host,
                                vpn = options.SessionProperties.VPNName,
                                code = sendReplyReturnCode
                            }))
                                logger.LogError("Error sending response.");
                    }

                    application.DisposeContext(context, null);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected error processing message.");
                    application.DisposeContext(context, ex);
                }
        }

        protected virtual void FillRequest(HttpRequest request, IMessage requestMessage)
        {
            request.HttpContext.Features.Set<ISolaceFeature>(new SolaceFeature(requestMessage));

            request.Method = HttpMethods.Post;
            request.Path = '/' + requestMessage.ApplicationMessageType;
            request.ContentLength = requestMessage.BinaryAttachment.Length;
            request.Body = new MemoryStream(requestMessage.BinaryAttachment);
        }

        protected virtual void FillResponse(HttpResponse response, IMessage responseMessage)
        {
            responseMessage.ApplicationMessageType =
                response.HttpContext.Features.Get<ISolaceFeature>().ResponseApplicationMessageType;
            responseMessage.BinaryAttachment = response.Body.ReadAllBytes();
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var _ = logger.BeginScope(new
            {
                host = options.SessionProperties.Host,
                vpn = options.SessionProperties.VPNName
            });

            logger.LogDebug("The server is stopping...");

            messageProcessingCancellation.Cancel();

            logger.LogDebug("Waiting for the message loop to exit...");

            try
            {
                await messageProcessingTask!;
            }
            catch (OperationCanceledException)
            { }

            logger.LogDebug("Disconnecting the session...");

            Session!.Disconnect();

            logger.LogInformation("The server has been stopped...");
        }

        public virtual void Dispose()
        {
            using var _ = logger.BeginScope(new
            {
                host = options.SessionProperties.Host,
                vpn = options.SessionProperties.VPNName
            });

            logger.LogDebug("Disposing...");

            messageProcessingCancellation.Dispose();
            contexts.Dispose();
            Session?.Dispose();

            foreach (var topic in topics)
                topic.Dispose();

            logger.LogDebug("Disposed...");
        }
    }
}
