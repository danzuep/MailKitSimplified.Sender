﻿using MimeKit;
using MailKit;
using MailKit.Search;
using MailKit.Net.Imap;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MailKitSimplified.Receiver.Abstractions;
using MailKitSimplified.Receiver.Extensions;
using MailKitSimplified.Receiver.Models;

namespace MailKitSimplified.Receiver.Services
{
    public sealed class MailFolderReader : IMailFolderReader
    {
        /// <summary>Core message summary items: UniqueId, Envelope, Headers, Size, and BodyStructure.</summary>
        [Obsolete("Use the IMailReader.ItemsForMimeMessages() extension instead.")]
        public static readonly MessageSummaryItems CoreMessageItems =
            MessageSummaryItems.UniqueId |
            MessageSummaryItems.Envelope |
            MessageSummaryItems.Headers |
            MessageSummaryItems.Size |
            MessageSummaryItems.BodyStructure;

        /// <summary>Query just the arrival dates of messages on the server.</summary>
        /// <param name="deliveredAfter">Search for messages after this date.</param>
        /// <param name="deliveredBefore">Search for messages before this date.</param>
        /// <returns><see cref="SearchQuery"/> with a maximum of 250 results.</returns>
        [Obsolete("Use the IMailReader.QueryBetweenDates() extension instead.")]
        public static SearchQuery QueryBetweenDates(DateTime deliveredAfter, DateTime? deliveredBefore = null)
        {
            DateTime before = deliveredBefore != null ? deliveredBefore.Value : DateTime.Now;
            var query = SearchQuery.DeliveredAfter(deliveredAfter).And(SearchQuery.DeliveredBefore(before));
            return query;
        }

        /// <summary>Query the server for messages with matching keywords in the subject or body text.</summary>
        /// <param name="keywords">Keywords to search for.</param>
        /// <returns><see cref="SearchQuery"/> with a maximum of 250 results.</returns>
        [Obsolete("Use the IMailReader.QueryKeywords() extension instead.")]
        public static SearchQuery QueryKeywords(IEnumerable<string> keywords)
        {
            var subjectMatch = keywords.MatchAny(SearchQuery.SubjectContains);
            var bodyMatch = keywords.MatchAny(SearchQuery.BodyContains);
            var query = subjectMatch.Or(bodyMatch);
            return query;
        }

        /// <summary>Query the server for message(s) with a matching message ID.</summary>
        /// <param name="messageId">Message-ID to search for.</param>
        /// <param name="addAngleBrackets">Angle brackets added by default.</param>
        /// <returns><see cref="SearchQuery"/> with a maximum of 250 results.</returns>
        [Obsolete("Use the IMailReader.QueryMessageId() extension instead.")]
        public static SearchQuery QueryMessageId(string messageId, bool addAngleBrackets = true)
        {
            var searchText = addAngleBrackets ? $"<{messageId}>" : messageId;
            var query = SearchQuery.HeaderContains("Message-Id", searchText);
            return query;
        }

        private int _skip = 0;
        private int _take = _all;
        private ushort? _top = null;
        private UniqueIdRange _uniqueIds = null;
        private bool _continueTake = false;
        private static readonly int _all = -1;
        private static readonly int _queryAmount = 250;
        private SearchQuery _searchQuery = _queryAll;
        private static readonly SearchQuery _queryAll = SearchQuery.All;
        private MessageSummaryItems _messageSummaryItems = MessageSummaryItems.Envelope;
        private readonly ILogger _logger;
        private readonly IImapReceiver _imapReceiver;

        public MailFolderReader(IImapReceiver imapReceiver, ILogger<MailFolderReader> logger = null)
        {
            _logger = logger ?? NullLogger<MailFolderReader>.Instance;
            _imapReceiver = imapReceiver ?? throw new ArgumentNullException(nameof(imapReceiver));
        }

        public static MailFolderReader Create(EmailReceiverOptions emailReceiverOptions, ILogger<MailFolderReader> logger = null, ILogger<ImapReceiver> logImap = null, IProtocolLogger protocolLogger = null, IImapClient imapClient = null)
        {
            if (emailReceiverOptions == null)
                throw new ArgumentNullException(nameof(emailReceiverOptions));
            var imapReceiver = ImapReceiver.Create(emailReceiverOptions, logImap, protocolLogger, imapClient);
            var mailFolderReader = new MailFolderReader(imapReceiver, logger);
            return mailFolderReader;
        }

        public static MailFolderReader Create(IImapClient imapClient, EmailReceiverOptions emailReceiverOptions, ILogger<MailFolderReader> logger = null, ILogger<ImapReceiver> logImap = null)
        {
            var imapReceiver = ImapReceiver.Create(imapClient, emailReceiverOptions, logImap);
            var mailFolderReader = new MailFolderReader(imapReceiver, logger);
            return mailFolderReader;
        }

        public IMailReader Skip(int skipCount)
        {
            if (skipCount < 0)
                throw new ArgumentOutOfRangeException(nameof(skipCount));
            _skip = skipCount;
            return this;
        }

        public IMailReader Take(int takeCount, bool continuous = false)
        {
            if (takeCount < -1)
                throw new ArgumentOutOfRangeException(nameof(takeCount));
            if (takeCount > ushort.MaxValue)
                _logger.LogWarning($"Take({takeCount}) should be split into smaller batches.");
            _take = takeCount;
            _continueTake = continuous;
            return this;
        }

        public IMailReader Top(ushort count)
        {
            if (count == 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            _top = count;
            return this;
        }

        public IMailReader Range(UniqueId start, UniqueId end, bool continuous = false)
        {
            _uniqueIds = new UniqueIdRange(start, end);
            _continueTake = continuous;
            return this;
        }

        public IMailReader Range(UniqueId start, ushort batchSize = 0, bool continuous = true)
        {
            unchecked
            {
                var endId = start.Id + batchSize;
                var end = endId < start.Id ? UniqueId.MaxValue : new UniqueId(endId);
                _uniqueIds = new UniqueIdRange(start, end);
            }
            _continueTake = continuous;
            return this;
        }

        public IMailReader Query(SearchQuery searchQuery)
        {
            _searchQuery = searchQuery;
            if (searchQuery != SearchQuery.All)
                _take = _queryAmount;
            return this;
        }

        public IMailReader Items(MessageSummaryItems messageSummaryItems)
        {
            _messageSummaryItems = messageSummaryItems | MessageSummaryItems.UniqueId;
            return this;
        }

        private static async Task<IEnumerable<UniqueId>> GetValidUniqueIdsAsync(IMailFolder mailFolder, IEnumerable<UniqueId> uniqueIds, CancellationToken cancellationToken = default)
        {
            var fetchRequest = new FetchRequest(MessageSummaryItems.UniqueId);
            var ascendingIds = uniqueIds is IList<UniqueId> ids ? ids : new UniqueIdSet(uniqueIds, SortOrder.Ascending);
            var messageSummaries = await mailFolder.FetchAsync(ascendingIds, fetchRequest, cancellationToken).ConfigureAwait(false);
            return messageSummaries.Select(m => m.UniqueId);
        }

        private async Task<(IMailFolder, bool)> OpenMailFolderAsync(CancellationToken cancellationToken = default)
        {
            if (_take == 0)
                _logger.LogDebug($"Opening mail folder, but next time don't use Take({_take})."); // return (null, false);
            var mailFolder = await _imapReceiver.ConnectMailFolderAsync(cancellationToken).ConfigureAwait(false);
            bool closeWhenFinished = !mailFolder.IsOpen;
            if (!mailFolder.IsOpen)
                _ = await mailFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            if (_skip >= mailFolder.Count || (_skip > _queryAmount && _searchQuery != _queryAll))
            {
                if (_skip <= mailFolder.Count)
                {
                    _logger.LogInformation($"Skip({_skip}) limited to mail folder count of {mailFolder.Count}.");
                    if (_continueTake)
                        _skip = mailFolder.Count;
                }
                else
                    _logger.LogWarning($"Skip({_skip}) exceeded SearchQuery limit of 250 results.");
            }
            return (mailFolder, closeWhenFinished);
        }

        private async Task CloseMailFolderAsync(IMailFolder mailFolder, bool close = true, int count = 1)
        {
            if (_continueTake && (count < 1 ||
                _uniqueIds == null && (_take <= 0 || _skip + (uint)_take > int.MaxValue) ||
                _uniqueIds != null && (_uniqueIds.End.Id == uint.MaxValue || _uniqueIds.End.Id < _uniqueIds.Start.Id)))
                _continueTake = false;
            if (_continueTake)
            {
                if (_uniqueIds != null)
                {
                    unchecked
                    {
                        var start = new UniqueId(_uniqueIds.End.Id + 1);
                        uint size = start.Id - _uniqueIds.Start.Id;
                        uint endId = _uniqueIds.End.Id + size;
                        var end = endId < _uniqueIds.End.Id ? UniqueId.MaxValue : new UniqueId(endId);
                        _uniqueIds = new UniqueIdRange(start, end);
                    }
                }
                else
                {
                    if (_skip < mailFolder.Count)
                        _skip += _take;
                    else
                        _skip = mailFolder.Count;
                }
            }
            else if (close)
                await mailFolder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task<IList<IMessageSummary>> GetMessageSummariesAsync(IMailFolder mailFolder, MessageSummaryItems filter, CancellationToken cancellationToken = default)
        {
            if (mailFolder == null || _take == 0)
            {
                if (_take == 0)
                    _logger.LogInformation($"Take({_take}) means no results will be returned.");
                return Array.Empty<IMessageSummary>();
            }
            IList<IMessageSummary> filteredSummaries;
            if (_uniqueIds != null)
            {
                filteredSummaries = await mailFolder.FetchAsync(_uniqueIds, filter, cancellationToken).ConfigureAwait(false);
            }
            else if (_searchQuery != _queryAll)
            {
                var searchOptions = SearchOptions.None;
                if (_take != _all && (uint)_skip + _take > _queryAmount)
                    _logger.LogWarning($"Skip({_skip}).Take({_take}) limited by SearchQuery to 250 results.");
                else if (_take == _all)
                    searchOptions = SearchOptions.All;
                var searchResults = await mailFolder.SearchAsync(searchOptions, _searchQuery, cancellationToken).ConfigureAwait(false);
                var takeAll = _take == _all || _take == _queryAmount;
                var noFilter = _skip == 0 && takeAll;
                var descendingUids = noFilter ? null :
                    new UniqueIdSet(searchResults.UniqueIds, SortOrder.Descending).Skip(_skip);
                var filteredUids = noFilter ? null :
                    descendingUids?.Take(_take);
                var ascendingUids = noFilter ? searchResults.UniqueIds :
                    new UniqueIdSet(filteredUids, SortOrder.Ascending);
                var messageSummaries = await mailFolder.FetchAsync(ascendingUids, filter, cancellationToken).ConfigureAwait(false);
                filteredSummaries = messageSummaries.Count > _queryAmount || messageSummaries.Count == ascendingUids.Count ?
                    messageSummaries : messageSummaries.Where(m => ascendingUids.Contains(m.UniqueId)).ToList();
            }
            else if (!_top.HasValue)
            {
                int endIndex = _take < 0 ? _all : _skip + _take - 1;
                filteredSummaries = await mailFolder.FetchAsync(_skip, endIndex, filter, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var endIndex = mailFolder.Count > 1 ? mailFolder.Count - 1 : 0;
                int startIndex = endIndex - (_top.Value - 1);
                var messageSummaries = await mailFolder.FetchAsync(startIndex, endIndex, filter, cancellationToken).ConfigureAwait(false);
                filteredSummaries = messageSummaries.Reverse().ToList();
            }
            _logger.LogTrace($"{_imapReceiver} received {filteredSummaries.Count} email(s).");
            if (_continueTake && _take > 0)
                _skip += _take;
            if (_continueTake && _take > 0)
            {
                if (_skip < mailFolder.Count)
                    _skip += _take;
                else
                    _skip = mailFolder.Count;
            }

            return filteredSummaries;
        }

        [Obsolete("Use Items().GetMessageSummariesAsync() instead.")]
        public async Task<IList<IMessageSummary>> GetMessageSummariesAsync(MessageSummaryItems filter, CancellationToken cancellationToken = default)
        {
            if (_take == 0)
            {
                _logger.LogInformation("Take(0) means no results will be returned.");
                return Array.Empty<IMessageSummary>();
            }
            filter |= MessageSummaryItems.UniqueId;
            (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
            var messageSummaries = await GetMessageSummariesAsync(mailFolder, filter, cancellationToken).ConfigureAwait(false);
            await CloseMailFolderAsync(mailFolder, closeWhenFinished, messageSummaries.Count).ConfigureAwait(false);
            return messageSummaries;
        }

        public async Task<IList<IMessageSummary>> GetMessageSummariesAsync(CancellationToken cancellationToken = default)
        {
            if (_take == 0)
            {
                _logger.LogInformation("Take(0) means no results will be returned.");
                return Array.Empty<IMessageSummary>();
            }
            (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
            var messageSummaries = await GetMessageSummariesAsync(mailFolder, _messageSummaryItems, cancellationToken).ConfigureAwait(false);
            await CloseMailFolderAsync(mailFolder, closeWhenFinished, messageSummaries.Count).ConfigureAwait(false);
            return messageSummaries;
        }

        public async Task<IList<MimeMessage>> GetMimeMessagesAsync(CancellationToken cancellationToken = default, ITransferProgress transferProgress = null)
        {
            IList<MimeMessage> mimeMessages = new List<MimeMessage>();
            if (_take == 0)
            {
                _logger.LogInformation("Take(0) means no results will be returned.");
                return mimeMessages;
            }
            (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
            if (_uniqueIds != null)
            {
                mimeMessages = await GetMimeMessagesAsync(mailFolder, _uniqueIds, cancellationToken, transferProgress).ConfigureAwait(false);
            }
            else if ((_take == _all && !_top.HasValue) || _searchQuery != _queryAll)
            {
                if (_take != _all && (uint)_skip + _take > _queryAmount)
                    _logger.LogWarning($"Skip({_skip}).Take({_take}) limited by SearchQuery to 250 results.");
                else if (_take == _all)
                    _logger.LogDebug("GetMimeMessagesAsync() limited by SearchQuery to 250 results.");
                var uniqueIds = await mailFolder.SearchAsync(_searchQuery, cancellationToken).ConfigureAwait(false);
                var descendingUids = new UniqueIdSet(uniqueIds, SortOrder.Descending).Skip(_skip);
                var filteredUids = _take == _all ? descendingUids : descendingUids.Take(_take);
                var ascendingUids = new UniqueIdSet(filteredUids, SortOrder.Ascending);
                foreach (var uniqueId in ascendingUids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mimeMessage = await mailFolder.GetMessageAsync(uniqueId, cancellationToken, transferProgress).ConfigureAwait(false);
                    if (mimeMessage != null)
                    {
                        _logger.LogTrace($"{_imapReceiver} received #{uniqueId}, {mimeMessage.MessageId}.");
                        mimeMessages.Add(mimeMessage);
                    }
                }
            }
            else if (!_top.HasValue)
            {
                int endIndex = _skip + _take > mailFolder.Count ? mailFolder.Count : _skip + _take;
                for (int index = _skip; index < endIndex; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mimeMessage = await mailFolder.GetMessageAsync(index, cancellationToken, transferProgress).ConfigureAwait(false);
                    if (mimeMessage != null)
                        mimeMessages.Add(mimeMessage);
                }
            }
            else
            {
                var endIndex = mailFolder.Count > 1 ? mailFolder.Count - 1 : 0;
                int startIndex = endIndex - (_top.Value - 1);
                for (int index = endIndex; index > startIndex; index--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mimeMessage = await mailFolder.GetMessageAsync(index, cancellationToken, transferProgress).ConfigureAwait(false);
                    if (mimeMessage != null)
                        mimeMessages.Add(mimeMessage);
                }
            }
            _logger.LogTrace($"{_imapReceiver} received {mimeMessages.Count} email(s).");
            await CloseMailFolderAsync(mailFolder, closeWhenFinished, mimeMessages.Count).ConfigureAwait(false);

            return mimeMessages;
        }

        private async Task<IList<IMessageSummary>> GetMessageSummariesAsync(IMailFolder mailFolder, IEnumerable<UniqueId> uniqueIds, MessageSummaryItems filter = MessageSummaryItems.UniqueId, CancellationToken cancellationToken = default)
        {
            IList<IMessageSummary> filteredSummaries = null;
            if (_take != 0 && uniqueIds != null)
            {
                var ascendingIds = uniqueIds is IList<UniqueId> ids ? ids : new UniqueIdSet(uniqueIds, SortOrder.Ascending);
                var messageSummaries = await mailFolder.FetchAsync(ascendingIds, filter, cancellationToken).ConfigureAwait(false);
                filteredSummaries = _uniqueIds != null || messageSummaries.Count > ushort.MaxValue ? messageSummaries.Reverse().ToList() :
                    messageSummaries.Where(m => ascendingIds.Contains(m.UniqueId)).Reverse().ToList();
            }
            return filteredSummaries ?? Array.Empty<IMessageSummary>();
        }

        [Obsolete("Use Range(UidStart, UidEnd).GetMessageSummariesAsync() instead.")]
        public async Task<IList<IMessageSummary>> GetMessageSummariesAsync(IEnumerable<UniqueId> uniqueIds, MessageSummaryItems filter = MessageSummaryItems.UniqueId, CancellationToken cancellationToken = default)
        {
            if (uniqueIds == null)
                return Array.Empty<IMessageSummary>();
            filter |= MessageSummaryItems.UniqueId;
            (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
            var filteredSummaries = await GetMessageSummariesAsync(mailFolder, uniqueIds, filter, cancellationToken).ConfigureAwait(false);
            _logger.LogTrace($"{_imapReceiver} received {filteredSummaries.Count} email(s).");
            await CloseMailFolderAsync(mailFolder, closeWhenFinished, filteredSummaries.Count).ConfigureAwait(false);
            return filteredSummaries;
        }

        [Obsolete("Use Range(uniqueId).GetMimeMessagesAsync() instead.")]
        public async Task<MimeMessage> GetMimeMessageAsync(UniqueId uniqueId, CancellationToken cancellationToken = default, ITransferProgress progress = null)
        {
            MimeMessage mimeMessage;
            (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
            mimeMessage = await mailFolder.GetMessageAsync(uniqueId, cancellationToken, progress).ConfigureAwait(false);
            _logger.LogTrace($"{_imapReceiver} received #{uniqueId}, {mimeMessage.MessageId}.");
            if (closeWhenFinished)
                await mailFolder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            return mimeMessage;
        }

        private async Task<IList<MimeMessage>> GetMimeMessagesAsync(IMailFolder mailFolder, IEnumerable<UniqueId> uniqueIds, CancellationToken cancellationToken = default, ITransferProgress progress = null)
        {
            IList<MimeMessage> mimeMessages = new List<MimeMessage>();
            if (mailFolder == null || uniqueIds == null)
                return mimeMessages;
            var filteredUids = await GetValidUniqueIdsAsync(mailFolder, uniqueIds, cancellationToken).ConfigureAwait(false);
            foreach (var uniqueId in filteredUids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var mimeMessage = await mailFolder.GetMessageAsync(uniqueId, cancellationToken, progress).ConfigureAwait(false);
                    if (mimeMessage != null)
                    {
                        _logger.LogTrace($"{_imapReceiver} received #{uniqueId}, {mimeMessage.MessageId}.");
                        mimeMessages.Add(mimeMessage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_imapReceiver} failed to get message #{uniqueId}.");
                    throw; // preserve the stack trace
                }
            }
            _logger.LogTrace($"{_imapReceiver} received {mimeMessages.Count} email(s).");
            return mimeMessages;
        }

        [Obsolete("Use Range(UidStart, UidEnd).GetMimeMessagesAsync() instead.")]
        public async Task<IList<MimeMessage>> GetMimeMessagesAsync(IEnumerable<UniqueId> uniqueIds, CancellationToken cancellationToken = default, ITransferProgress progress = null)
        {
            IList<MimeMessage> mimeMessages = null;
            if (uniqueIds != null)
            {
                (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
                mimeMessages = await GetMimeMessagesAsync(mailFolder, uniqueIds, cancellationToken, progress).ConfigureAwait(false);
                await CloseMailFolderAsync(mailFolder, closeWhenFinished, mimeMessages.Count).ConfigureAwait(false);
            }
            return mimeMessages ?? new List<MimeMessage>();
        }

        [Obsolete("Use messageSummary.GetMimeMessageEnvelopeBodyAsync() instead.")]
        public async Task<MimeMessage> GetMimeMessageEnvelopeBodyAsync(IMessageSummary messageSummary, CancellationToken cancellationToken = default)
        {
            var mimeMessage = new MimeMessage();
            if (messageSummary == null)
                return mimeMessage;

            // Add message Envelope parts
            if (messageSummary.Envelope != null)
            {
                mimeMessage.Subject = messageSummary.Envelope.Subject;
                mimeMessage.From.AddRange(messageSummary.Envelope.From);
                if (messageSummary.Envelope.Sender.Mailboxes.FirstOrDefault() is MailboxAddress sender)
                    mimeMessage.Sender = sender;
                mimeMessage.ReplyTo.AddRange(messageSummary.Envelope.ReplyTo);
                mimeMessage.To.AddRange(messageSummary.Envelope.To);
                mimeMessage.Cc.AddRange(messageSummary.Envelope.Cc);
                mimeMessage.Bcc.AddRange(messageSummary.Envelope.Bcc);
                mimeMessage.MessageId = messageSummary.Envelope.MessageId;
                if (messageSummary.Envelope.Date.HasValue)
                    mimeMessage.Date = messageSummary.Envelope.Date.Value;
            }

            // Add message References
            if (messageSummary.References != null)
                mimeMessage.References.AddRange(messageSummary.References);

            // Add message TextBody and HtmlBody parts
            bool peekFolder = !messageSummary.Folder.IsOpen;
            if (peekFolder || messageSummary.Folder.Access == FolderAccess.None)
                _ = await messageSummary.Folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            MimeEntity textEntity = null;
            if (messageSummary.HtmlBody is BodyPartText htmlBody)
                textEntity = await messageSummary.Folder.GetBodyPartAsync(messageSummary.UniqueId, htmlBody, cancellationToken);
            else if (messageSummary.TextBody is BodyPartText textBody)
                textEntity = await messageSummary.Folder.GetBodyPartAsync(messageSummary.UniqueId, textBody, cancellationToken);

            if (textEntity is TextPart bodyText)
                mimeMessage.Body = bodyText;

            if (peekFolder)
                await messageSummary.Folder.CloseAsync(expunge: false, cancellationToken).ConfigureAwait(false);
            _logger.LogTrace($"{_imapReceiver} received {mimeMessage.MessageId} ({messageSummary.Folder.FullName} {messageSummary.Index}).");

            return mimeMessage;
        }

        private async Task<IList<MimeMessage>> GetMimeMessagesEnvelopeBodyAsync(IMailFolder mailFolder, IEnumerable<UniqueId> uniqueIds, CancellationToken cancellationToken = default)
        {
            IList<MimeMessage> mimeMessages = new List<MimeMessage>();
            if (mailFolder == null || uniqueIds == null)
            {
                if (mailFolder == null)
                    _logger.LogInformation("MailFolder(null) means no results will be returned.");
                if (uniqueIds == null)
                    _logger.LogInformation("Range(null) means no results will be returned.");
                return mimeMessages;
            }
            var filter = MessageSummaryItems.Envelope | MessageSummaryItems.References | MessageSummaryItems.BodyStructure;
            var ascendingIds = uniqueIds is IList<UniqueId> ids ? ids : new UniqueIdSet(uniqueIds, SortOrder.Ascending);
            var messageSummaries = await mailFolder.FetchAsync(ascendingIds, filter, cancellationToken).ConfigureAwait(false);
            foreach (var messageSummary in messageSummaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mimeMessage = await messageSummary.GetMimeMessageEnvelopeBodyAsync(cancellationToken).ConfigureAwait(false);
                mimeMessages.Add(mimeMessage);
                _logger.LogTrace($"{_imapReceiver} received {mimeMessage.MessageId} ({messageSummary.Folder.FullName} {messageSummary.Index}).");
            }
            return mimeMessages;
        }

        [Obsolete("Use Range(UidStart, UidEnd).GetMimeMessagesEnvelopeBodyAsync() instead.")]
        public async Task<IList<MimeMessage>> GetMimeMessagesEnvelopeBodyAsync(IEnumerable<UniqueId> uniqueIds, CancellationToken cancellationToken = default)
        {
            IList<MimeMessage> mimeMessages = new List<MimeMessage>();
            if (uniqueIds == null)
            {
                _logger.LogInformation($"Range(null) means no results will be returned.");
                return mimeMessages;
            }
            (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
            await GetMimeMessagesEnvelopeBodyAsync(mailFolder, uniqueIds, cancellationToken).ConfigureAwait(false);
            await CloseMailFolderAsync(mailFolder, closeWhenFinished, mimeMessages.Count).ConfigureAwait(false);
            return mimeMessages;
        }

        //[Obsolete("Use ItemsForMimeMessages().GetMimeMessagesAsync() instead.")]
        public async Task<IList<MimeMessage>> GetMimeMessagesEnvelopeBodyAsync(CancellationToken cancellationToken = default)
        {
            IList<MimeMessage> mimeMessages = new List<MimeMessage>();
            if (_take == 0)
            {
                _logger.LogInformation("Take(0) means no results will be returned.");
                return mimeMessages;
            }
            (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
            if (_uniqueIds != null)
            {
                mimeMessages = await GetMimeMessagesEnvelopeBodyAsync(mailFolder, _uniqueIds, cancellationToken).ConfigureAwait(false);
            }
            else if (_searchQuery != _queryAll)
            {
                if (_take != _all && (uint)_skip + _take > _queryAmount)
                    _logger.LogWarning($"Skip({_skip}).Take({_take}) limited by SearchQuery to 250 results.");
                else if (_take == _all)
                    _logger.LogDebug("GetMimeMessagesAsync() limited by SearchQuery to 250 results.");
                var uniqueIds = await mailFolder.SearchAsync(_searchQuery, cancellationToken).ConfigureAwait(false);
                mimeMessages = await GetMimeMessagesEnvelopeBodyAsync(mailFolder, uniqueIds, cancellationToken).ConfigureAwait(false);
            }
            else if (_top.HasValue)
            {
                var endIndex = mailFolder.Count > 1 ? mailFolder.Count - 1 : 0;
                int startIndex = endIndex - (_top.Value - 1);
                var filter = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.References | MessageSummaryItems.BodyStructure;
                var messageSummaries = await mailFolder.FetchAsync(startIndex, endIndex, filter, cancellationToken).ConfigureAwait(false);
                foreach (var messageSummary in messageSummaries.Reverse())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mimeMessage = await messageSummary.GetMimeMessageEnvelopeBodyAsync(cancellationToken).ConfigureAwait(false);
                    mimeMessages.Add(mimeMessage);
                    _logger.LogTrace($"{_imapReceiver} received {mimeMessage.MessageId} ({messageSummary.Folder.FullName} {messageSummary.Index}).");
                }
                return mimeMessages;
            }
            else
            {
                _logger.LogInformation("Range(null) means no results will be returned.");
                throw new NotImplementedException();
            }
            _logger.LogTrace($"{_imapReceiver} received {mimeMessages.Count} email(s).");
            await CloseMailFolderAsync(mailFolder, closeWhenFinished, mimeMessages.Count).ConfigureAwait(false);
            return mimeMessages;
        }

#if NET5_0_OR_GREATER
        [Obsolete("Use Range(UidStart, UidEnd).GetMessageSummariesAsync() instead.")]
        public async IAsyncEnumerable<IList<IMessageSummary>> GetMessageSummariesAsync(uint startUid, ushort batchSize, MessageSummaryItems filter = MessageSummaryItems.UniqueId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
            for (uint start = startUid; start <= uint.MaxValue; start += batchSize)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                uint endUid = start + batchSize - 1;
                var range = new UniqueIdRange(new UniqueId(start), new UniqueId(endUid));
                var messageSummaries = await mailFolder.FetchAsync(range, filter, cancellationToken).ConfigureAwait(false);
                _logger.LogTrace($"{_imapReceiver} received {messageSummaries.Count} messages(s).");
                yield return messageSummaries;
            }
            if (closeWhenFinished)
                await mailFolder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
        }

        [Obsolete("Use Range(UidStart, UidEnd).GetMimeMessages() instead.")]
        public async IAsyncEnumerable<MimeMessage> GetMimeMessages(IEnumerable<UniqueId> uniqueIds, [EnumeratorCancellation] CancellationToken cancellationToken = default, ITransferProgress progress = null)
        {
            if (uniqueIds != null)
            {
                (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
                var filteredUids = await GetValidUniqueIdsAsync(mailFolder, uniqueIds, cancellationToken).ConfigureAwait(false);
                foreach (var uniqueId in filteredUids)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    var mimeMessage = await mailFolder.GetMessageAsync(uniqueId, cancellationToken, progress).ConfigureAwait(false);
                    _logger.LogTrace($"{_imapReceiver} received #{uniqueId}, {mimeMessage.MessageId}.");
                    if (mimeMessage != null)
                        yield return mimeMessage;
                }
                if (closeWhenFinished)
                    await mailFolder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }

        [Obsolete("Use Range(UidStart, UidEnd).GetMimeMessagesEnvelopeBody() instead.")]
        public async IAsyncEnumerable<MimeMessage> GetMimeMessagesEnvelopeBody(IEnumerable<UniqueId> uniqueIds, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
            var ascendingIds = uniqueIds is IList<UniqueId> ids ? ids : new UniqueIdSet(uniqueIds, SortOrder.Ascending);
            var filter = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.References | MessageSummaryItems.BodyStructure;
            var messageSummaries = await mailFolder.FetchAsync(ascendingIds, filter, cancellationToken).ConfigureAwait(false);
            foreach (var messageSummary in messageSummaries)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                var mimeMessage = await messageSummary.GetMimeMessageEnvelopeBodyAsync(cancellationToken).ConfigureAwait(false);
                yield return mimeMessage;
            }
            if (closeWhenFinished)
                await mailFolder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
        }

        [Obsolete("Use Range(UidStart, UidEnd).SaveAllAsync() instead.")]
        public async Task SaveAllAsync(IEnumerable<UniqueId> uniqueIds, string folderPath, bool createDirectory = false, CancellationToken cancellationToken = default, ITransferProgress progress = null)
        {
            if (createDirectory)
                Directory.CreateDirectory(folderPath);
            else if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException($"Directory not found: {folderPath}.");
            var format = FormatOptions.Default.Clone();
            format.NewLineFormat = NewLineFormat.Dos;
            await foreach (var mimeMessage in GetMimeMessages(uniqueIds, cancellationToken, progress))
            {
                string fileName = Path.Combine(folderPath, $"{mimeMessage.MessageId}.eml");
                await mimeMessage.WriteToAsync(format, fileName, cancellationToken).ConfigureAwait(false);
            }
        }

        public async IAsyncEnumerable<MimeMessage> GetMimeMessagesEnvelopeBody([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_uniqueIds == null)
                _logger.LogInformation("Range(null) means no results will be returned.");
            else
            {
                (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
                var filter = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.References | MessageSummaryItems.BodyStructure;
                var messageSummaries = await mailFolder.FetchAsync(_uniqueIds, filter, cancellationToken).ConfigureAwait(false);
                foreach (var messageSummary in messageSummaries)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    var mimeMessage = await messageSummary.GetMimeMessageEnvelopeBodyAsync(cancellationToken).ConfigureAwait(false);
                    yield return mimeMessage;
                }
                await CloseMailFolderAsync(mailFolder, closeWhenFinished, messageSummaries.Count).ConfigureAwait(false);
            }
        }

        public async IAsyncEnumerable<MimeMessage> GetMimeMessages([EnumeratorCancellation] CancellationToken cancellationToken = default, ITransferProgress progress = null)
        {
            if (_uniqueIds == null)
                _logger.LogInformation("Range(null) means no results will be returned.");
            else
            {
                int count = 0;
                (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
                var filteredUids = await GetValidUniqueIdsAsync(mailFolder, _uniqueIds, cancellationToken).ConfigureAwait(false);
                foreach (var uniqueId in filteredUids)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    var mimeMessage = await mailFolder.GetMessageAsync(uniqueId, cancellationToken, progress).ConfigureAwait(false);
                    _logger.LogTrace($"{_imapReceiver} received #{uniqueId}, {mimeMessage.MessageId}.");
                    yield return mimeMessage;
                    count++;
                }
                await CloseMailFolderAsync(mailFolder, closeWhenFinished, count).ConfigureAwait(false);
            }
        }

        public async Task SaveAllAsync(string folderPath, bool createDirectory = false, CancellationToken cancellationToken = default, ITransferProgress progress = null)
        {
            if (_uniqueIds == null)
                _logger.LogInformation("Range(null) means no results will be returned.");
            else
            {
                if (createDirectory)
                    Directory.CreateDirectory(folderPath);
                else if (!Directory.Exists(folderPath))
                    throw new DirectoryNotFoundException($"Directory not found: {folderPath}.");
                var format = FormatOptions.Default.Clone();
                format.NewLineFormat = NewLineFormat.Dos;
                await foreach (var mimeMessage in GetMimeMessages(cancellationToken, progress))
                {
                    string fileName = Path.Combine(folderPath, $"{mimeMessage.MessageId}.eml");
                    await mimeMessage.WriteToAsync(format, fileName, cancellationToken).ConfigureAwait(false);
                }
            }
        }
#else
        [Obsolete("Use Range(UidStart, UidEnd).SaveAllAsync() instead.")]
        public async Task SaveAllAsync(IEnumerable<UniqueId> uniqueIds, string folderPath, bool createDirectory = false, CancellationToken cancellationToken = default, ITransferProgress progress = null)
        {
            if (uniqueIds != null)
            {
                if (createDirectory)
                    Directory.CreateDirectory(folderPath);
                else if (!Directory.Exists(folderPath))
                    throw new DirectoryNotFoundException($"Directory not found: {folderPath}.");
                var format = FormatOptions.Default.Clone();
                format.NewLineFormat = NewLineFormat.Dos;
                (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
                var filteredUids = await GetValidUniqueIdsAsync(mailFolder, uniqueIds, cancellationToken).ConfigureAwait(false);
                foreach (var uniqueId in filteredUids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mimeMessage = await mailFolder.GetMessageAsync(uniqueId, cancellationToken, progress).ConfigureAwait(false);
                    _logger.LogTrace($"{_imapReceiver} received {mimeMessage.MessageId}.");
                    if (mimeMessage != null)
                    {
                        string fileName = Path.Combine(folderPath, $"{mimeMessage.MessageId}.eml");
                        await mimeMessage.WriteToAsync(format, fileName, cancellationToken).ConfigureAwait(false);
                    }
                }
                if (closeWhenFinished)
                    await mailFolder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }

        public async Task SaveAllAsync(string folderPath, bool createDirectory = false, CancellationToken cancellationToken = default, ITransferProgress progress = null)
        {
            if (_uniqueIds == null)
                _logger.LogInformation("Range(null) means no results will be returned.");
            else
            {
                if (createDirectory)
                    Directory.CreateDirectory(folderPath);
                else if (!Directory.Exists(folderPath))
                    throw new DirectoryNotFoundException($"Directory not found: {folderPath}.");
                var format = FormatOptions.Default.Clone();
                format.NewLineFormat = NewLineFormat.Dos;
                (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
                var filteredUids = await GetValidUniqueIdsAsync(mailFolder, _uniqueIds, cancellationToken).ConfigureAwait(false);
                foreach (var uniqueId in filteredUids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mimeMessage = await mailFolder.GetMessageAsync(uniqueId, cancellationToken, progress).ConfigureAwait(false);
                    _logger.LogTrace($"{_imapReceiver} received {mimeMessage.MessageId}.");
                    if (mimeMessage != null)
                    {
                        string fileName = Path.Combine(folderPath, $"{mimeMessage.MessageId}.eml");
                        await mimeMessage.WriteToAsync(format, fileName, cancellationToken).ConfigureAwait(false);
                    }
                }
                if (closeWhenFinished)
                    await mailFolder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }

        public async Task ProcessMessageSummariesAsync(uint startUid, ushort batchSize, Func<IMessageSummary, CancellationToken, Task> ProcessMessages, MessageSummaryItems filter = MessageSummaryItems.UniqueId, CancellationToken cancellationToken = default)
        {
            (var mailFolder, var closeWhenFinished) = await OpenMailFolderAsync(cancellationToken).ConfigureAwait(false);
            IList<IMessageSummary> messageSummaries;
            do
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                uint endUid = startUid + batchSize - 1;
                var range = new UniqueIdRange(new UniqueId(startUid), new UniqueId(endUid));
                messageSummaries = await mailFolder.FetchAsync(range, filter, cancellationToken).ConfigureAwait(false);
                _logger.LogTrace($"{_imapReceiver} received {messageSummaries.Count} messages(s).");
                foreach (var messageSummary in messageSummaries)
                {
                    await ProcessMessages(messageSummary, cancellationToken);
                }
                startUid += batchSize;
            }
            while (messageSummaries.Count > 0);
            if (closeWhenFinished)
                await mailFolder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
        }
#endif
        /// <summary>Query just the arrival dates of messages on the server.</summary>
        /// <param name="deliveredAfter">Search for messages after this date.</param>
        /// <param name="deliveredBefore">Search for messages before this date.</param>
        /// <returns>List of <see cref="IMessageSummary"/> items.</returns>
        [Obsolete("Use MailFolderReader.Query().GetMessageSummariesAsync() or the IMailReader.QueryBetweenDates() extension instead.")]
        public async Task<IList<IMessageSummary>> SearchBetweenDatesAsync(DateTime deliveredAfter, DateTime? deliveredBefore = null, CancellationToken cancellationToken = default)
        {
            this.QueryBetweenDates(deliveredAfter, deliveredBefore);
            var messageSummaries = await GetMessageSummariesAsync(cancellationToken).ConfigureAwait(false);
            return messageSummaries;
        }

        /// <summary>Query the server for message IDs with matching keywords in the subject or body text.</summary>
        /// <param name="keywords">Keywords to search for.</param>
        /// <returns>List of <see cref="IMessageSummary"/> items.</returns>
        [Obsolete("Use MailFolderReader.Query().GetMessageSummariesAsync() or the IMailReader.QueryKeywords() extension instead.")]
        public async Task<IList<IMessageSummary>> SearchKeywordsAsync(IEnumerable<string> keywords, CancellationToken cancellationToken = default)
        {
            this.QueryKeywords(keywords);
            var messageSummaries = await GetMessageSummariesAsync(cancellationToken).ConfigureAwait(false);
            return messageSummaries;
        }

        /// <summary>Query the server for message(s) with a matching message ID.</summary>
        /// <param name="messageId">Message-ID to search for.</param>
        /// <param name="addAngleBrackets">Angle brackets added by default.</param>
        /// <returns>List of <see cref="IMessageSummary"/> items.</returns>
        [Obsolete("Use MailFolderReader.Query().GetMessageSummariesAsync() or the IMailReader.QueryMessageId() extension instead.")]
        public async Task<IList<IMessageSummary>> SearchMessageIdAsync(string messageId, bool addAngleBrackets = true, CancellationToken cancellationToken = default)
        {
            this.QueryMessageId(messageId, addAngleBrackets);
            var messageSummaries = await GetMessageSummariesAsync(cancellationToken).ConfigureAwait(false);
            return messageSummaries;
        }

        public IMailFolderReader Copy() => MemberwiseClone() as IMailFolderReader;

        public override string ToString() => $"{_imapReceiver} (skip {_skip}, take {_take})";

        public async ValueTask DisposeAsync() => await _imapReceiver.DisposeAsync().ConfigureAwait(false);

        public void Dispose() => _imapReceiver.Dispose();
    }
}
