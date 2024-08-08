using MailKitSimplified.Receiver;
using ExampleNamespace;

using var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<Worker>();
        services.AddScopedMailKitSimplifiedEmailReceiver(context.Configuration);
    })
    .Build();

await host.RunAsync();