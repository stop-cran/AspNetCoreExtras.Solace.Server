using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolaceSystems.Solclient.Messaging;
using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCoreExtras.Solace.Server
{
    public class ObservableSession : IObservableSession, IHostedService
    {
        private readonly IContext context;
        private readonly IOptions<SessionProperties> options;
        private readonly ILogger<ObservableSession> logger;

        private readonly Subject<IMessage> messages = new Subject<IMessage>();
        private readonly Subject<SessionEventArgs> sessionEvents = new Subject<SessionEventArgs>();

        public ObservableSession(IContext context, IOptions<SessionProperties> options, ILogger<ObservableSession> logger)
        {
            this.context = context;
            this.options = options;
            this.logger = logger;
        }

        public ISession? Session { get; private set; }
        public IObservable<IMessage> Messages => messages.ObserveOn(TaskPoolScheduler.Default);
        public IObservable<SessionEventArgs> SessionEvents => sessionEvents.ObserveOn(TaskPoolScheduler.Default);

        public virtual async Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sessionProperties = options.Value;
            using var _ = logger.BeginScope(new
            {
                host = sessionProperties.Host,
                vpn = sessionProperties.VPNName
            });

            sessionProperties.ConnectBlocking = false;
            logger.LogDebug("Creating session...");

            Session = context.CreateSession(options.Value,
                (sender, e) => messages.OnNext(e.Message),
                (sender, e) =>
                {
                    LogSessionEvent(e);
                    sessionEvents.OnNext(e);
                });

            logger.LogDebug("Connecting session...");

            var upNotice = SessionEvents
                .FirstAsync(e => e.Event == SessionEvent.UpNotice)
                .RunAsync(cancellationToken);
            var connectReturnCode = Session.Connect();

            if (connectReturnCode == ReturnCode.SOLCLIENT_IN_PROGRESS)
            {
                await upNotice;
                logger.LogInformation("Connected successfully...");
            }
            else
            {
                upNotice.Dispose();

                using (logger.BeginScope(new
                {
                    userName = sessionProperties.UserName,
                    code = connectReturnCode
                }))
                    logger.LogError("Error connecting Solace.");

                throw new ApplicationException("Error connecting Solace.");
            }
        }

        public virtual async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Session == null)
                throw new InvalidOperationException("The session has not been connected.");

            using var _ = logger.BeginScope(new
            {
                host = Session.Properties.Host,
                vpn = Session.Properties.VPNName
            });

            logger.LogDebug("Disconnecting the session...");

            var disconnected = SessionEvents
                .FirstAsync(e => e.Event == SessionEvent.DownError)
                .RunAsync(cancellationToken);
            var code = Session.Disconnect();

            if (code == ReturnCode.SOLCLIENT_IN_PROGRESS)
            {
                await disconnected;
                logger.LogInformation("Disconnected.");
            }
            else
                using (logger.BeginScope(new { code }))
                    logger.LogInformation("Error disconnecting.");

            logger.LogInformation("The server has been stopped...");
        }

        private void LogSessionEvent(SessionEventArgs args)
        {
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

        public virtual void Dispose()
        {
            if (Session != null)
            {
                using var _ = logger.BeginScope(new
                {
                    host = Session.Properties.Host,
                    vpn = Session.Properties.VPNName
                });

                logger.LogDebug("Disposing...");

                Session.Dispose();

                logger.LogDebug("Disposed...");
            }
        }
    }
}
