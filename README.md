# Overview [![NuGet](https://img.shields.io/nuget/v/AspNetCoreExtras.Solace.Server.svg)](https://www.nuget.org/packages/AspNetCoreExtras.Solace.Server) [![Actions Status](../../workflows/.NET%20Core/badge.svg)](../../actions) [![Coverage Status](https://coveralls.io/repos/github/stop-cran/AspNetCoreExtras.Solace.Server/badge.svg?branch=master)](https://coveralls.io/github/stop-cran/AspNetCoreExtras.Solace.Server?branch=master)

An extension for ASP.Net Core that allows to process messages from [Solace Pub-Sub](https://solace.com).

# Installation

NuGet package is available [here](https://www.nuget.org/packages/AspNetCoreExtras.Solace.Server/).

```PowerShell
PM> Install-Package AspNetCoreExtras.Solace.Server
```

# Use case

Use all ASP.Net infrastructure including formatters, routing, dependency injection and logging, to process messages from Solace message broker in request/reply mode.

```C#
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // Read Solace settings from the app config.
        services.Configure<SolaceServerOptions>(Configuration.GetSection("Solace"));
        services.Configure<SessionProperties>(Configuration.GetSection("Solace:SessionProperties"));
        // Process Solace messages, rather than HTTP requests.
        // This call reqisters `SolaceServer` as ASP.Net `IServer` implementation.
        // To process both HTTP and Solace in a single app, one can create
        // kind of composite class as `IServer` implementation (see below).
        services.AddSolaceServer();
        // Other settings.
    }
}
```

appconfig.json sample (for other session properties see [documentation](https://docs.solace.com/API-Developer-Online-Ref-Documentation/net/html/82816aab-350c-a890-cc35-ac125b35421c.htm)):

```JSON
{
  "Solace": {
    "Topics": [ "someTopic" ],
    "SessionProperties": {
      "Host": "solace.host",
      "VPNName": "solace-vpn",
      "UserName": "solace-user",
      "Password": "***",
      "ConnectRetries": 10,
      "ConnectTimeoutInMsecs": 10000,
      "ReconnectRetries": 10,
      "ReconnectRetriesWaitInMsecs": 1000
    }
  }
}
```

Sample controller:

```C#
public class SampleSolaceController : Controller
{
    [HttpPost("/mySolaceApplicationMessageType")]
    public IActionResult Post([FromBody]MySolaceParameters parameters)
    {
        HttpContext.Features.Get<ISolaceFeature>()
            .ResponseApplicationMessageType =
            "responseApplicationMessageType";

        return Ok(new MySolaceResponse
        {
            AnotherInt = 321,
            AnotherString = "fgsfds"
        });
    }
}

public class MySolaceParameters
{
    public string MyString { get; set; }
    public int MyInt { get; set; }
}


public class MySolaceResponse
{
    public string AnotherString { get; set; }
    public int AnotherInt { get; set; }
}
```

Sample call by C# Solace client:

```C#
static void Main()
{
    ContextFactory.Instance.Init(new ContextFactoryProperties());

    using var context = ContextFactory.Instance.CreateContext(new ContextProperties(), null);
    using var session = context.CreateSession(new SessionProperties
    {
        Host = "solace.host",
        VPNName = "solace-vpn",
        UserName = "solace-user",
        Password = "***"
    }, null, null);

    using var topic = ContextFactory.Instance.CreateTopic("someTopic");

    var code = session.Connect();

    using var message = session.CreateMessage();

    message.ApplicationMessageType = "mySolaceApplicationMessageType";
    message.BinaryAttachment = Encoding.UTF8.GetBytes("{ \"MyString\": \"qwerty\", \"MyInt\": 12345 }");
    message.Destination = topic;

    var code2 = session.SendRequest(message, out var response, 10000);
    var responseBodyString = Encoding.UTF8.GetString(response.BinaryAttachment);
    var responseApplicationMessageType = response.ApplicationMessageType.ToString();
}
```

Sample composite server implementation to process both HTTP and Solace requests:

```C#
public class SolaceAndKestrelServer : IServer
{
    private readonly SolaceServer solaceServer;
    private readonly KestrelServer kestrelServer;

    public SolaceAndKestrelServer(SolaceServer solaceServer, KestrelServer kestrelServer)
    {
        this.solaceServer = solaceServer;
        this.kestrelServer = kestrelServer;
    }

    public IFeatureCollection Features => kestrelServer.Features;

    public async Task StartAsync<TContext>(
        IHttpApplication<TContext> application,
        CancellationToken cancellationToken)
    {
        await solaceServer.StartAsync(application, cancellationToken);
        await kestrelServer.StartAsync(application, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await solaceServer.StopAsync(cancellationToken);
        await kestrelServer.StopAsync(cancellationToken);
    }

    public void Dispose()
    {
        solaceServer.Dispose();
        kestrelServer.Dispose();
    }
}
```

Startup services configuration:

```C#
public void ConfigureServices(IServiceCollection services)
{
    // Read Solace settings from the app config.
    services.Configure<SolaceServerOptions>(Configuration.GetSection("Solace"));
    services.Configure<SessionProperties>(Configuration.GetSection("Solace:SessionProperties"));
    services.AddSolaceContext();
    services.AddSolaceObservableSession();
    services.AddSingleton<SolaceServer>();
    services.AddSingleton<KestrelServer>();
    services.AddSingleton<IServer, SolaceAndKestrelServer>();
    // Other calls.
}
```