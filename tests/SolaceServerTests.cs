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
using System.Reactive.Subjects;
using System.Threading.Tasks;

namespace AspNetCoreExtras.Solace.Server.Tests
{
    public class SolaceServerTests
    {
        private Mock<SolaceSystems.Solclient.Messaging.ISession> session = null!;
        private Mock<IObservableSession> observableSession = null!;
        private Subject<IMessage> messages = null!;
        private Subject<SessionEventArgs> sessionEvents = null!;
        private Mock<IOptions<SolaceServerOptions>> options = null!;
        private Mock<ILogger<SolaceServer>> logger = null!;
        private Mock<IHttpApplication<ApplicationContextMock>> application = null!;
        private Mock<HttpContext> httpContext = null!;
        private Mock<HttpRequest> httpRequest = null!;
        private Mock<HttpResponse> httpResponse = null!;
        private SolaceServer solaceServer = null!;

        [SetUp]
        public void Setup()
        {
            session = new Mock<SolaceSystems.Solclient.Messaging.ISession>();
            observableSession = new Mock<IObservableSession>();
            messages = new Subject<IMessage>();
            sessionEvents = new Subject<SessionEventArgs>();
            options = new Mock<IOptions<SolaceServerOptions>>();
            application = new Mock<IHttpApplication<ApplicationContextMock>>();
            logger = new Mock<ILogger<SolaceServer>>();
            httpContext = new Mock<HttpContext>();
            httpRequest = new Mock<HttpRequest>();
            httpResponse = new Mock<HttpResponse>();

            observableSession.Setup(s => s.Session).Returns(() => session.Object);
            observableSession.Setup(s => s.Messages).Returns(messages);
            observableSession.Setup(s => s.SessionEvents).Returns(sessionEvents);

            session.Setup(s => s.CreateMessage())
                .Returns(Mock.Of<IMessage>());
            session.Setup(s => s.Properties)
                .Returns(new SessionProperties());

            options.Setup(op => op.Value)
                .Returns(new SolaceServerOptions
                {
                    Topics = new System.Collections.Generic.List<string> { "testTopic" }
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

            solaceServer = new SolaceServer(observableSession.Object, options.Object, logger.Object);
        }

        [Test]
        public async Task ShouldSubscribeConfiguredTopic()
        {
            await solaceServer.StartAsync(application.Object, default);

            session.Setup(s => s.Subscribe(It.IsAny<ISubscription>(), It.IsAny<bool>()))
                .Callback((ISubscription subscription, bool wait) =>
                subscription.ShouldBeOfType<ITopic>().Name.ShouldBe("testTopic"));
        }

        [Test]
        public async Task ShouldLogSubscriptionError()
        {
            session.Setup(s => s.Subscribe(It.IsAny<ISubscription>(), It.IsAny<bool>()))
                .Returns(ReturnCode.SOLCLIENT_FAIL);

            await solaceServer.StartAsync(application.Object, default);

            logger.Verify(log => log.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception, string>>()));
        }

        [Test]
        public async Task ShouldProcessMessage()
        {
            var message = CreateMessageMock();

            message.Setup(m => m.ReplyTo)
                .Returns(Mock.Of<IDestination>());

            await solaceServer.StartAsync(application.Object, default);

            messages.OnNext(message.Object);

            await Task.Delay(100);

            application.Verify(a => a.ProcessRequestAsync(It.IsAny<ApplicationContextMock>()));
        }

        [Test]
        public async Task ShouldProcessTwoMessages()
        {
            var message = CreateMessageMock();

            message.Setup(m => m.ReplyTo)
                .Returns(Mock.Of<IDestination>());

            await solaceServer.StartAsync(application.Object, default);

            messages.OnNext(message.Object);
            messages.OnNext(message.Object);

            await Task.Delay(100);

            application.Verify(a => a.ProcessRequestAsync(It.IsAny<ApplicationContextMock>()), Times.Exactly(2));
        }

        [Test, TestCase("test")]
        public async Task ShouldAssignSolaceFeature(string applicationMessageType)
        {
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

            await solaceServer.StartAsync(application.Object, default);

            messages.OnNext(message.Object);

            await Task.Delay(100);

            application.Verify(a => a.ProcessRequestAsync(It.IsAny<ApplicationContextMock>()));
        }

        [Test]
        public async Task ShouldProcessMessageOneWay()
        {
            var message = CreateMessageMock();

            await solaceServer.StartAsync(application.Object, default);

            messages.OnNext(message.Object);

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

        public class ApplicationContextMock
        {
            public HttpContext? HttpContext { get; set; }
        }
    }
}