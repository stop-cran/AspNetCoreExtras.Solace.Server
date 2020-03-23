using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Shouldly;
using SolaceSystems.Solclient.Messaging;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace AspNetCoreExtras.Solace.Server.Tests
{
    public class Tests
    {
        private Mock<IContext> context = new Mock<IContext>();
        private Mock<SolaceSystems.Solclient.Messaging.ISession> session = new Mock<SolaceSystems.Solclient.Messaging.ISession>();
        private Mock<IOptions<SolaceServerOptions>> options = new Mock<IOptions<SolaceServerOptions>>();
        private Mock<ILogger<SolaceServer>> logger = new Mock<ILogger<SolaceServer>>();
        private EventHandler<MessageEventArgs>? onMessage;
        private Mock<IHttpApplication<ApplicationContextMock>> application = new Mock<IHttpApplication<ApplicationContextMock>>();
        private Mock<HttpContext> httpContext = new Mock<HttpContext>();
        private Mock<HttpRequest> httpRequest = new Mock<HttpRequest>();
        private Mock<HttpResponse> httpResponse = new Mock<HttpResponse>();

        [SetUp]
        public void Setup()
        {
            onMessage = null;
            context = new Mock<IContext>();
            options = new Mock<IOptions<SolaceServerOptions>>();
            application = new Mock<IHttpApplication<ApplicationContextMock>>();
            logger = new Mock<ILogger<SolaceServer>>();
            httpContext = new Mock<HttpContext>();
            httpRequest = new Mock<HttpRequest>();
            httpResponse = new Mock<HttpResponse>();

            context.Setup(c => c.CreateSession(
                It.IsAny<SessionProperties>(),
                It.IsAny<EventHandler<MessageEventArgs>>(),
                It.IsAny<EventHandler<SessionEventArgs>>()))
                .Callback((SessionProperties sessionProperties, EventHandler<MessageEventArgs> onMessage, EventHandler<SessionEventArgs> onSessionEvent) =>
                {
                    this.onMessage = onMessage;
                })
                .Returns(session.Object);

            session.Setup(s => s.CreateMessage())
                .Returns(Mock.Of<IMessage>());

            options.Setup(op => op.Value)
                .Returns(new SolaceServerOptions
                {
                    Solace = new SolaceSettings
                    {
                        Topics = new[] { "testTopic" },
                        MaxParallelRequests = 1
                    }
                });
            application.Setup(a => a.CreateContext(It.IsAny<FeatureCollection>()))
                .Returns(new ApplicationContextMock
                {
                    HttpContext = httpContext.Object
                });

            httpRequest.SetupAllProperties();
            httpRequest.Setup(r => r.Headers)
                .Returns(Mock.Of<IHeaderDictionary>());
            httpResponse.SetupAllProperties();
            httpResponse.Setup(r => r.Headers)
                .Returns(Mock.Of<IHeaderDictionary>());
            httpContext.Setup(c => c.Request)
                .Returns(httpRequest.Object);
            httpContext.Setup(c => c.Response)
                .Returns(httpResponse.Object);
        }

        [Test]
        public async Task ShouldLogConnectionError()
        {
            session.Setup(s => s.Connect())
                .Returns(ReturnCode.SOLCLIENT_FAIL);

            var server = CreateServer();

            await server.StartAsync(application.Object, default);

            logger.Verify(log => log.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception, string>>()));
        }

        [Test]
        public async Task ShouldSubscribeConfiguredTopic()
        {
            session.Setup(s => s.Connect())
                .Returns(ReturnCode.SOLCLIENT_OK);

            var server = CreateServer();

            await server.StartAsync(application.Object, default);

            session.Setup(s => s.Subscribe(It.IsAny<ISubscription>(), It.IsAny<bool>()))
                .Callback((ISubscription subscription, bool wait) =>
                subscription.ShouldBeOfType<ITopic>().Name.ShouldBe("testTopic"));
        }

        [Test]
        public async Task ShouldLogSubscriptionError()
        {
            session.Setup(s => s.Connect())
                .Returns(ReturnCode.SOLCLIENT_OK);

            session.Setup(s => s.Subscribe(It.IsAny<ISubscription>(), It.IsAny<bool>()))
                .Returns(ReturnCode.SOLCLIENT_FAIL);

            var server = CreateServer();

            await server.StartAsync(application.Object, default);

            logger.Verify(log => log.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception, string>>()));
        }

        [Test]
        public async Task ShouldDisconnect()
        {
            session.Setup(s => s.Connect())
                .Returns(ReturnCode.SOLCLIENT_OK);

            var server = CreateServer();

            await server.StartAsync(application.Object, default);
            await server.StopAsync(default);

            session.Verify(s => s.Disconnect());
        }

        [Test]
        public async Task ShouldDisposeSession()
        {
            session.Setup(s => s.Connect())
                .Returns(ReturnCode.SOLCLIENT_OK);

            var server = CreateServer();

            await server.StartAsync(application.Object, default);
            await server.StopAsync(default);

            server.Dispose();

            session.Verify(s => s.Dispose());
        }

        [Test]
        public async Task ShouldProcessMessage()
        {
            session.Setup(s => s.Connect())
                .Returns(ReturnCode.SOLCLIENT_OK);

            var server = CreateServer();

            await server.StartAsync(application.Object, default);

            var message = new Mock<IMessage>();

            message.Setup(m => m.Destination)
                .Returns(Mock.Of<IDestination>());

            var constructor = typeof(MessageEventArgs).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(IMessage), typeof(BaseProperties) },
                null);
            var eventArgs = (MessageEventArgs)constructor.Invoke(
                new object[]
                {
                    message.Object,
                    Mock.Of<BaseProperties>()
                });

            onMessage(session.Object, eventArgs);

            await Task.Delay(100);

            application.Verify(a => a.ProcessRequestAsync(It.IsAny<ApplicationContextMock>()));
        }

        private SolaceServer CreateServer() =>
            new SolaceServer(context.Object, options.Object, logger.Object);


        public class ApplicationContextMock
        {
            public HttpContext HttpContext { get; set; }
        }
    }
}