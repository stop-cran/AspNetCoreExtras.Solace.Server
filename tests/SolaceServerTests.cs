using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Shouldly;
using SolaceSystems.Solclient.Messaging;
using System;
using System.Reflection;
using System.Threading;
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
            session = new Mock<SolaceSystems.Solclient.Messaging.ISession>();
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
                .Returns(() => session.Object);

            session.Setup(s => s.CreateMessage())
                .Returns(Mock.Of<IMessage>());

            options.Setup(op => op.Value)
                .Returns(new SolaceServerOptions
                {
                    Topics = new[] { "testTopic" },
                    MaxParallelRequests = 1
                });
            application.Setup(a => a.CreateContext(It.IsAny<FeatureCollection>()))
                .Returns(() => new ApplicationContextMock
                {
                    HttpContext = httpContext.Object
                });

            httpRequest.SetupAllProperties();
            httpRequest.Setup(r => r.HttpContext)
                .Returns(() => httpContext.Object);
            httpResponse.SetupAllProperties();
            httpResponse.Setup(r => r.HttpContext)
                .Returns(() => httpContext.Object);
            httpContext.Setup(c => c.Request)
                .Returns(() => httpRequest.Object);
            httpContext.Setup(c => c.Response)
                .Returns(() => httpResponse.Object);
            httpContext.SetupGet(c => c.Features)
                .Returns(new FeatureCollection());
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
            var message = CreateMessageMock();

            message.Setup(m => m.ReplyTo)
                .Returns(Mock.Of<IDestination>());

            await server.StartAsync(application.Object, default);

            onMessage.ShouldNotBeNull();

            onMessage!(session.Object, CreateMessageEventArgs(message.Object));

            await Task.Delay(100);

            application.Verify(a => a.ProcessRequestAsync(It.IsAny<ApplicationContextMock>()));
        }

        [Test, TestCase("test")]
        public async Task ShouldAssignSolaceFeature(string applicationMessageType)
        {
            session.Setup(s => s.Connect())
                .Returns(ReturnCode.SOLCLIENT_OK);

            var server = CreateServer();
            var message = CreateMessageMock();

            message.Setup(m => m.ReplyTo)
                .Returns(Mock.Of<IDestination>());
            message.Setup(m => m.ApplicationMessageType)
                .Returns(applicationMessageType);
            application.Setup(a => a.ProcessRequestAsync(It.IsAny<ApplicationContextMock>()))
                .Callback<ApplicationContextMock>(context =>
                {
                    context.HttpContext.ShouldNotBeNull();

                    var feature = context.HttpContext!.Features.Get<ISolaceFeature>();

                    feature.ShouldNotBeNull();
                    feature.RequestApplicationMessageType.ShouldBe(applicationMessageType);
                });

            await server.StartAsync(application.Object, default);

            onMessage.ShouldNotBeNull();

            onMessage!(session.Object, CreateMessageEventArgs(message.Object));

            await Task.Delay(100);

            application.Verify(a => a.ProcessRequestAsync(It.IsAny<ApplicationContextMock>()));
        }

        [Test]
        public async Task ShouldProcessMessageOneWay()
        {
            session.Setup(s => s.Connect())
                .Returns(ReturnCode.SOLCLIENT_OK);

            var server = CreateServer();
            var message = CreateMessageMock();

            await server.StartAsync(application.Object, default);

            onMessage.ShouldNotBeNull();

            onMessage!(session.Object, CreateMessageEventArgs(message.Object));

            await Task.Delay(100);

            application.Verify(a => a.ProcessRequestAsync(It.IsAny<ApplicationContextMock>()));
            session.Verify(s => s.CreateMessage(), Times.Never());
        }

        private Mock<IMessage> CreateMessageMock()
        {
            var message = new Mock<IMessage>();

            message.Setup(m => m.Destination)
                .Returns(Mock.Of<IDestination>());

            return message;
        }

        private MessageEventArgs CreateMessageEventArgs(IMessage message)
        {
            var constructor = typeof(MessageEventArgs).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(IMessage), typeof(BaseProperties) },
                null);

            constructor.ShouldNotBeNull();

            return (MessageEventArgs)constructor!.Invoke(
                new object[]
                {
                    message,
                    Mock.Of<BaseProperties>()
                });
        }

        private SolaceServer CreateServer() =>
            new SolaceServer(context.Object, options.Object, logger.Object);


        public class ApplicationContextMock
        {
            public HttpContext? HttpContext { get; set; }
        }
    }
}