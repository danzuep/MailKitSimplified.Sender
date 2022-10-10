using MailKitSimplified.Core.Abstractions;
using MailKitSimplified.Core.Models;
using MailKitSimplified.Core.Services;
using MailKitSimplified.Sender.Services;
using WorkerServiceExample;

public class Program
{
    public static async Task Main(string[] args)
    {
        IHost host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                services.AddHostedService<Worker>();
                ConfigureServices(services, context.Configuration);
            })
            .Build();

        await host.RunAsync();
    }

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // This adds IOptions<EmailSenderOptions> from appsettings.json
        services.Configure<EmailSenderOptions>(configuration
            .GetRequiredSection(EmailSenderOptions.SectionName));
        services.AddScoped<IEmail, Email>();
        services.AddScoped<IEmailWriter, EmailWriter>();
        services.AddScoped<IEmailSender, MimeMessageSender>();
    }
}