using MailKit;
using MailKitSimplified.Receiver.Abstractions;

namespace ExampleNamespace;

public class Worker : BackgroundService
{
    private readonly IServiceScope _serviceScope;
    private readonly IImapReceiver _imapReceiver;
    private readonly ILogger<Worker> _logger;

    public Worker(IServiceScopeFactory serviceScopeFactory, ILogger<Worker> logger)
    {
        _serviceScope = serviceScopeFactory.CreateScope();
        _imapReceiver = _serviceScope.ServiceProvider.GetRequiredService<IImapReceiver>();
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        //await AddFlagsToNewestMessageSummaryAsync(cancellationToken);
        await ImapReceiverFactoryAsync(cancellationToken);
    }

    private async Task ImapReceiverFactoryAsync(CancellationToken cancellationToken = default)
    {
        var imapReceiverFactory = _serviceScope.ServiceProvider.GetRequiredService<IImapReceiverFactory>();
        var receivers = imapReceiverFactory.GetAllImapReceivers();
        foreach (var receiver in receivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var messageSummaries = await receiver.ReadMail.GetMessageSummariesAsync(cancellationToken);
            int count = 0;
            foreach (var messageSummary in messageSummaries)
            {
                if (++count > 10) break;
                _logger.LogInformation($"{receiver} message #{count}: {messageSummary.UniqueId}");
            }
        }
    }

    private async Task MailFolderMonitorFactoryAsync(CancellationToken cancellationToken = default)
    {
        var mailFolderMonitorFactory = _serviceScope.ServiceProvider.GetRequiredService<IMailFolderMonitorFactory>();
        void LogUniqueIdArrived(IMessageSummary messageSummary) =>
            _logger.LogInformation($"Message #{messageSummary.UniqueId} arrived.");
        await mailFolderMonitorFactory.MonitorAllMailboxesAsync(LogUniqueIdArrived, cancellationToken);
    }

    public async Task<int?> AddFlagsToNewestMessageSummaryAsync(CancellationToken cancellationToken = default)
    {
        using var mailFolderClient = _serviceScope.ServiceProvider.GetRequiredService<IMailFolderClient>();
        var messageSummary = await GetTopMessageSummaryAsync(cancellationToken);
        var uniqueId = await mailFolderClient.AddFlagsAsync([messageSummary.UniqueId], MessageFlags.Seen);
        return uniqueId;
    }

    private async Task<IMessageSummary> GetTopMessageSummaryAsync(CancellationToken cancellationToken = default)
    {
        using var mailFolderReader = _serviceScope.ServiceProvider.GetRequiredService<IMailFolderReader>();
        var messageSummaries = await mailFolderReader.Top(1).GetMessageSummariesAsync(cancellationToken);
        _logger.LogInformation($"{mailFolderReader} top message summary returned");
        return messageSummaries.Single();
    }

    public override void Dispose()
    {
        _serviceScope.Dispose();
        base.Dispose();
    }
}