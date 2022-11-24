﻿using MimeKit;
using MailKit;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace MailKitSimplified.Receiver.Abstractions
{
    public interface IMailReader
    {
        /// <summary>
        /// Offset to start getting messages from.
        /// </summary>
        /// <param name="skipCount">Offset to start getting messages from.</param>
        /// <param name="continuous">Whether to keep adding the offset or not.</param>
        /// <returns>Fluent <see cref="IMailReader"/>.</returns>
        IMailReader Skip(int skipCount, bool continuous = false);

        /// <summary>
        /// Number of messages to return.
        /// </summary>
        /// <param name="takeCount">Number of messages to return.</param>
        /// <returns>Fluent <see cref="IMailReader"/>.</returns>
        IMailReader Take(int takeCount);

        /// <summary>
        /// Get a list of the message summaries with just the requested MessageSummaryItems.
        /// </summary>
        /// <param name="filter"><see cref="MessageSummaryItems"/> to download.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        /// <returns>List of <see cref="IMessageSummary"/> items.</returns>
        ValueTask<IList<IMessageSummary>> GetMessageSummariesAsync(MessageSummaryItems filter, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get a list of the message summaries with basic MessageSummaryItems.
        /// </summary>
        /// <param name="cancellationToken">Request cancellation token.</param>
        /// <returns>List of <see cref="IMessageSummary"/> items.</returns>
        ValueTask<IList<IMessageSummary>> GetMessageSummariesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Get a list of <see cref="MimeMessage"/>s.
        /// </summary>
        /// <param name="cancellationToken">Request cancellation token.</param>
        /// <param name="transferProgress">Current email download progress</param>
        /// <returns>List of all <see cref="MimeMessage"/> items.</returns>
        ValueTask<IList<MimeMessage>> GetMimeMessagesAsync(CancellationToken cancellationToken = default, ITransferProgress transferProgress = null);
    }
}