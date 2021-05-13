using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolaceSystems.Solclient.Messaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCoreExtras.Solace.Server
{
    public class SolaceServer : IServer
    {
        private readonly IObservableSession session;
        private readonly IOptions<SolaceServerOptions> options;
        private readonly ILogger<SolaceServer> logger;
        private readonly List<ITopic> topics = new List<ITopic>();

        private readonly CancellationTokenSource messageProcessingCancellation =
            new CancellationTokenSource();

        private IDisposable? loggingScope;
        private AsyncSubject<Unit>? messageProcessingSubject;

        public SolaceServer(IObservableSession session, IOptions<SolaceServerOptions> options, ILogger<SolaceServer> logger)
        {
            this.session = session;
            this.options = options;
            this.logger = logger;

            Features.Set<IHttpRequestFeature>(new HttpRequestFeature());
            Features.Set<IHttpResponseFeature>(new HttpResponseFeature());
            Features.Set<ISolaceFeature>(
                new SolaceFeature("", ContextFactory.Instance.CreateTopic(""), null));
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public async Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        {
            logger.LogDebug("The server is starting...");

            if (session.Session == null)
                throw new InvalidOperationException("The session has not been started.");

            loggingScope?.Dispose();
            loggingScope = logger.BeginScope(new
            {
                host = session.Session.Properties.Host,
                vpn = session.Session.Properties.VPNName
            });

            logger.LogDebug("Creating topics...");

            var addressFeature = new ServerAddressesFeature();

            foreach (var topic in options.Value.Topics)
            {
                topics.Add(ContextFactory.Instance.CreateTopic(topic));
                addressFeature.Addresses.Add(topic);
            }

            Features.Set<IServerAddressesFeature>(addressFeature);

            foreach (var topic in topics)
            {
                using var scope = logger.BeginScope(new { topic = topic.Name });

                logger.LogDebug("Subscribing...");

                var subscribed = session.SessionEvents
                    .FirstAsync(e =>
                        e.Event == SessionEvent.SubscriptionOk ||
                        e.Event == SessionEvent.SubscriptionError)
                    .RunAsync(cancellationToken);
                var subscribeReturnCode = session.Session.Subscribe(topic, false);

                if (subscribeReturnCode == ReturnCode.SOLCLIENT_OK)
                    logger.LogDebug("Subscribed Solace topic.");
                else if (subscribeReturnCode == ReturnCode.SOLCLIENT_IN_PROGRESS)
                {
                    var subscribedEvent = await subscribed;

                    if (subscribedEvent.Event == SessionEvent.SubscriptionOk)
                        logger.LogDebug("Subscribed Solace topic.");
                    else
                        using (logger.BeginScope(new { sessionEvent = subscribedEvent.Event }))
                            logger.LogError("Error subscribing Solace topic: " + subscribedEvent.Info);
                    subscribed.Dispose();
                }
                else
                {
                    using (logger.BeginScope(new { code = subscribeReturnCode }))
                        logger.LogError("Error subscribing Solace topic.");
                    subscribed.Dispose();
                }
            }

            logger.LogDebug("Starting message processing loop...");

            messageProcessingSubject = session
                .Messages
                .Where(ShouldProcessMessage)
                .SelectMany(message => ProcessMessages(application, message, GetData(message)))
                .RunAsync(messageProcessingCancellation.Token);

            logger.LogInformation("The server has started successfully.");
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (session.Session == null)
                throw new InvalidOperationException("The session has not been started.");
            if (messageProcessingSubject == null)
                throw new InvalidOperationException("The server has not been started.");

            logger.LogDebug("The server is stopping...");

            messageProcessingCancellation.Cancel();

            logger.LogDebug("Waiting for the message loop to exit...");

            try
            {
                await messageProcessingSubject;
            }
            catch (OperationCanceledException)
            { }

            logger.LogInformation("The server has been stopped...");
        }

        protected virtual object? GetData(IMessage message) => null;

        protected virtual bool ShouldProcessMessage(IMessage message) =>
            !message.IsReplyMessage;

        private async Task<Unit> ProcessMessages<TContext>(IHttpApplication<TContext> application, IMessage message, object? data)
        {
            try
            {
                var context = application.CreateContext(new FeatureCollection(Features))!;

                try
                {
                    var httpContext = HttpApplicationContextHelper<TContext>.GetHttpContext(context);
                    using var responseStream = new MemoryStream();
                    using var traceScope = logger.BeginScope(new { traceIdentifier = httpContext.TraceIdentifier });

                    logger.LogDebug("Got a request.");
                    FillRequest(httpContext.Request, message, data);

                    httpContext.Response.ContentType = httpContext.Request.ContentType;
                    httpContext.Response.Body = responseStream;

                    logger.LogDebug("Processing the request...");
                    await application.ProcessRequestAsync(context);

                    if (message.ReplyTo == null)
                        logger.LogDebug("Skipped sending the response.");
                    else
                    {
                        logger.LogDebug("Sending the response...");
                        using var responseMessage = session.Session!.CreateMessage();

                        FillResponse(httpContext.Response, responseMessage);

                        var sendReplyReturnCode = session.Session.SendReply(message, responseMessage);

                        if (sendReplyReturnCode != ReturnCode.SOLCLIENT_OK)
                            using (logger.BeginScope(new { code = sendReplyReturnCode }))
                                logger.LogError("Error sending response.");
                    }

                    application.DisposeContext(context, null);
                }
                catch (Exception ex)
                {
                    application.DisposeContext(context, ex);
                    throw;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error processing message.");
            }

            return Unit.Default;
        }

        protected virtual void FillRequest(HttpRequest request, IMessage requestMessage, object? data)
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

        public virtual void Dispose()
        {
            logger.LogDebug("Disposing...");

            messageProcessingCancellation.Dispose();

            foreach (var topic in topics)
                topic.Dispose();

            logger.LogDebug("Disposed...");
            loggingScope?.Dispose();
        }
    }
}
