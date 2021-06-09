using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Shouldly;
using SolaceSystems.Solclient.Messaging;

namespace AspNetCoreExtras.Solace.Server.Tests
{
    public class IntegrationTests
    {
        private IHost host;

        [SetUp]
        public void Setup()
        {
            var sessionPropertiesOptions = new Mock<IOptions<SessionProperties>>();

            sessionPropertiesOptions.Setup(op => op.Value)
                .Returns(new SessionProperties
                {
                    Host = "localhost",
                    VPNName = "default",
                    UserName = "default",
                    Password = "default"
                });
            var solaceServerOptions = new Mock<IOptions<SolaceServerOptions>>();

            solaceServerOptions.Setup(op => op.Value)
                .Returns(new SolaceServerOptions
                {
                    Topics = new List<string> {"someTopic"}
                });

            host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                    webBuilder.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                            endpoints.MapPost("/123", async context =>
                            {
                                TestContext.WriteLine("Got a request: " +
                                                      Encoding.UTF8.GetString(context.Request.Body.ReadAllBytes()));
                                context.Features.Get<ISolaceFeature>().ResponseApplicationMessageType = "456";
                                await context.Response.WriteAsync(
                                    "test response");
                            }));
                    }).ConfigureServices(services => services
                        .AddTransient(_ => sessionPropertiesOptions.Object)
                        .AddTransient(_ => solaceServerOptions.Object)
                        .AddSolaceServer()
                        .AddLogging(l => l.ClearProviders().AddConsole())))
                .Build();
        }

        [Test]
        public async Task ShouldProcessRequest()
        {
            await host.StartAsync();

            using var context = ContextFactory.Instance.CreateContext(new ContextProperties(), null);
            using var session = context.CreateSession(new SessionProperties
            {
                Host = "localhost",
                VPNName = "default",
                UserName = "default",
                Password = "default"
            }, null, null);

            session.Connect().ShouldBe(ReturnCode.SOLCLIENT_OK);

            using var message = session.CreateMessage();
            using var topic = ContextFactory.Instance.CreateTopic("someTopic");

            message.ApplicationMessageType = "123";
            message.BinaryAttachment = Encoding.UTF8.GetBytes("test request");
            message.Destination = topic;

            for (int i = 0; i < 10; i++)
            {
                var code = session.SendRequest(message, out var response, 1000);

                if (code == ReturnCode.SOLCLIENT_INCOMPLETE)
                    continue;
                code.ShouldBe(ReturnCode.SOLCLIENT_OK);
                response.ApplicationMessageType.ShouldBe("456");
                response.Dispose();
            }

            await host.StopAsync();
        }

        [TearDown]
        public void TearDown()
        {
            host.Dispose();
        }
    }
}