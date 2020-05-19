using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolaceSystems.Solclient.Messaging;

namespace AspNetCoreExtras.Solace.Server
{
    public static class ServiceCollectionSolaceExtensions
    {
        /// <summary>
        /// Register Solace server, IObservableSession and SolaceSystems.Solclient.Messaging.IContext (see AddSolaceObservableSession method).
        /// </summary>
        public static IServiceCollection AddSolaceServer(this IServiceCollection services) =>
            services.AddSolaceObservableSession()
                .AddSingleton<IServer, SolaceServer>();

        /// <summary>
        /// Register IObservableSession and SolaceSystems.Solclient.Messaging.IContext (see AddSolaceContext method).
        /// </summary>
        public static IServiceCollection AddSolaceObservableSession(this IServiceCollection services) =>
            services.AddSolaceContext()
                .AddSingleton<IObservableSessionService, ObservableSession>()
                .AddSingleton<IObservableSession>(services => services.GetRequiredService<IObservableSessionService>());

        /// <summary>
        /// Register singleton SolaceSystems.Solclient.Messaging.IContext,
        /// initialize ContextFactory and forward Solace logging to
        /// Microsoft.Extensions.Logging.
        /// </summary>
        public static IServiceCollection AddSolaceContext(this IServiceCollection services) =>
            services
            .AddSingleton(provider =>
            {
                var contextFactorlogger = provider.GetRequiredService<ILogger<ContextFactory>>();
                var contextLogger = provider.GetRequiredService<ILogger<IContext>>();

                ContextFactory.Instance.Init(new ContextFactoryProperties
                {
                    SolClientLogLevel = provider.GetRequiredService<IOptions<SolaceServerOptions>>()
                        .Value.SolClientLogLevel,
                    LogDelegate = logInfo => OnSolaceEvent(logInfo, contextFactorlogger)
                });

                return ContextFactory.Instance.CreateContext(new ContextProperties(),
                    (sender, e) => OnContextEvent(e, contextLogger));
            });

        private static void OnSolaceEvent(SolLogInfo info, ILogger<ContextFactory> logger)
        {
            switch (info.LogLevel)
            {
                case SolLogLevel.Emergency:
                case SolLogLevel.Alert:
                case SolLogLevel.Critical:
                case SolLogLevel.Error:
                    if (info.LogException == null)
                        logger.LogError("{0}: {1}", info.LoggerName, info.LogMessage);
                    else
                        logger.LogError(info.LogException, "{0}: {1}", info.LoggerName, info.LogMessage);
                    break;

                case SolLogLevel.Warning:
                    if (info.LogException == null)
                        logger.LogWarning("{0}: {1}", info.LoggerName, info.LogMessage);
                    else
                        logger.LogWarning(info.LogException, "{0}: {1}", info.LoggerName, info.LogMessage);
                    break;
            }
        }

        private static void OnContextEvent(ContextEventArgs args, ILogger<IContext> logger)
        {
            if (args.Exception == null)
                logger.LogError("Context-level error: {0}.", args.ErrorInfo);
            else
                logger.LogError(args.Exception, "Context-level error: {0}.", args.ErrorInfo);
        }
    }
}
