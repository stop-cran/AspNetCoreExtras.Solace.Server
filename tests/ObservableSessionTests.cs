using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Shouldly;
using SolaceSystems.Solclient.Messaging;
using System;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCoreExtras.Solace.Server.Tests
{
    public class ObservableSessionTests
    {
        private Mock<IContext> context = null!;
        private Mock<ISession> session = null!;
        private Mock<IOptions<SessionProperties>> options = null!;
        private Mock<ILogger<ObservableSession>> logger = null!;
        private EventHandler<MessageEventArgs>? onMessage;
        private EventHandler<SessionEventArgs>? onSessionEvent;
        private CancellationTokenSource cancel = null!;
        private ObservableSession observableSession = null!;

        [SetUp]
        public void Setup()
        {
            onMessage = null;
            onSessionEvent = null;
            context = new Mock<IContext>();
            session = new Mock<ISession>();
            options = new Mock<IOptions<SessionProperties>>();
            logger = new Mock<ILogger<ObservableSession>>();
            cancel = new CancellationTokenSource(1000);

            context.Setup(c => c.CreateSession(
                It.IsAny<SessionProperties>(),
                It.IsAny<EventHandler<MessageEventArgs>>(),
                It.IsAny<EventHandler<SessionEventArgs>>()))
                .Callback((SessionProperties sessionProperties, EventHandler<MessageEventArgs> onMessage, EventHandler<SessionEventArgs> onSessionEvent) =>
                {
                    this.onMessage = onMessage;
                    this.onSessionEvent = onSessionEvent;
                })
                .Returns(() => session.Object);

            options.Setup(op => op.Value)
                .Returns(new SessionProperties());
            session.Setup(s => s.Properties)
                .Returns(new SessionProperties());
            session.Setup(s => s.Connect())
                .Callback(() => onSessionEvent?.Invoke(null, CreateSessionEventArgs(SessionEvent.UpNotice)))
                .Returns(ReturnCode.SOLCLIENT_IN_PROGRESS);
            observableSession = new ObservableSession(context.Object, options.Object, logger.Object);
        }

        [TearDown]
        public void TearDown()
        {
            cancel.Dispose();
        }

        [Test]
        public async Task ShouldLogConnectionError()
        {
            session.Setup(s => s.Connect())
                .Returns(ReturnCode.SOLCLIENT_FAIL);

            var task = observableSession.StartAsync(cancel.Token);

            await Should.ThrowAsync<ApplicationException>(task);
        }

        [Test]
        public async Task ShouldSubscribeConfiguredTopic()
        {
            await observableSession.StartAsync(cancel.Token);

            session.Setup(s => s.Subscribe(It.IsAny<ISubscription>(), It.IsAny<bool>()))
                .Callback((ISubscription subscription, bool wait) =>
                subscription.ShouldBeOfType<ITopic>().Name.ShouldBe("testTopic"));
        }

        [Test]
        public async Task ShouldDisconnect()
        {
            await observableSession.StartAsync(cancel.Token);
            await observableSession.StopAsync(cancel.Token);

            session.Verify(s => s.Disconnect());
        }

        [Test]
        public async Task ShouldDisposeSession()
        {
            await observableSession.StartAsync(cancel.Token);
            await observableSession.StopAsync(cancel.Token);

            observableSession.Dispose();

            session.Verify(s => s.Dispose());
        }

        [Test]
        public async Task ShouldProcessMessage()
        {
            var message = CreateMessageMock();

            message.Setup(m => m.ApplicationMessageType).Returns("1");

            await observableSession.StartAsync(cancel.Token);

            onMessage.ShouldNotBeNull();

            var gotMessageSubject = observableSession.Messages.FirstAsync().RunAsync(cancel.Token);

            onMessage!(session.Object, CreateMessageEventArgs(message.Object));

            await Task.Delay(100);

            var gotMessage = await gotMessageSubject;
            gotMessage.ApplicationMessageType.ShouldBe("1");
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

        private SessionEventArgs CreateSessionEventArgs(SessionEvent sessionEvent)
        {
            var constructor = typeof(SessionEventArgs).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(int), typeof(string), typeof(SessionEvent), typeof(object), typeof(IProperties) },
                null);

            constructor.ShouldNotBeNull();

            return (SessionEventArgs)constructor!.Invoke(
                new object[]
                {
                    (int)sessionEvent,
                    "",
                    sessionEvent,
                    new object(),
                    Mock.Of<IProperties>()
                });
        }
    }
}