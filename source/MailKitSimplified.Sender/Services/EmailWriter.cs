﻿using MimeKit;
using MimeKit.Text;
using MimeKit.Utils;
using MailKit;
using System;
using System.Linq;
using System.IO;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using MailKitSimplified.Sender.Abstractions;
using MailKitSimplified.Sender.Helpers;
using MailKitSimplified.Sender.Extensions;

namespace MailKitSimplified.Sender.Services
{
    public class EmailWriter : IEmailWriter
    {
        public MimeMessage MimeMessage => _mimeMessage;
        private MimeMessage _mimeMessage = new MimeMessage();
        private readonly ILogger _logger;
        private readonly ISmtpSender _emailClient;
        private readonly IFileSystem _fileSystem;

        public EmailWriter(ISmtpSender emailClient, ILogger<EmailWriter> logger = null, IFileSystem fileSystem = null)
        {
            _logger = logger ?? NullLogger<EmailWriter>.Instance;
            _emailClient = emailClient ?? throw new ArgumentNullException(nameof(emailClient));
            _fileSystem = fileSystem ?? new FileSystem();
        }

        public IEmailWriter From(string name, string address, bool replyTo = true)
        {
            var fromMailboxAddress = new MailboxAddress(name, address);
            _mimeMessage.From.Add(fromMailboxAddress);
            if (replyTo)
                _mimeMessage.ReplyTo.Add(fromMailboxAddress);
            return this;
        }

        public IEmailWriter From(string addresses, bool replyTo = true)
        {
            var mailboxAddresses = MailboxAddressHelper.ParseEmailContacts(addresses);
            _mimeMessage.From.AddRange(mailboxAddresses);
            if (replyTo)
                _mimeMessage.ReplyTo.AddRange(mailboxAddresses);
            return this;
        }

        public IEmailWriter To(string name, string address)
        {
            _mimeMessage.To.Add(new MailboxAddress(name, address));
            return this;
        }

        public IEmailWriter To(string addresses)
        {
            var mailboxAddresses = MailboxAddressHelper.ParseEmailContacts(addresses);
            _mimeMessage.To.AddRange(mailboxAddresses);
            return this;
        }

        public IEmailWriter Cc(string name, string address)
        {
            _mimeMessage.Cc.Add(new MailboxAddress(name, address));
            return this;
        }

        public IEmailWriter Cc(string addresses)
        {
            var mailboxAddresses = MailboxAddressHelper.ParseEmailContacts(addresses);
            _mimeMessage.Cc.AddRange(mailboxAddresses);
            return this;
        }

        public IEmailWriter Bcc(string name, string address)
        {
            _mimeMessage.Bcc.Add(new MailboxAddress(name, address));
            return this;
        }

        public IEmailWriter Bcc(string addresses)
        {
            var mailboxAddresses = MailboxAddressHelper.ParseEmailContacts(addresses);
            _mimeMessage.Bcc.AddRange(mailboxAddresses);
            return this;
        }

        public IEmailWriter Subject(string subject, bool append = false)
        {
            if (_mimeMessage.Subject == null || !append)
                _mimeMessage.Subject = subject ?? string.Empty;
            else
                _mimeMessage.Subject = $"{_mimeMessage.Subject}{subject}";
            return this;
        }

        public IEmailWriter BodyText(string textPlain) => Body(textPlain, false);

        public IEmailWriter BodyHtml(string textHtml) => Body(textHtml, true);

        private IEmailWriter Body(string bodyText, bool isHtml)
        {
            if (_mimeMessage.Body == null)
            {
                var format = isHtml ? TextFormat.Html : TextFormat.Plain;
                _mimeMessage.Body = new TextPart(format) { Text = bodyText ?? "" };
            }
            else
            {
                var builder = BuildMessage(_mimeMessage);
                if (isHtml)
                    builder.HtmlBody = bodyText;
                else
                    builder.TextBody = bodyText;
                _mimeMessage.Body = builder.ToMessageBody();
            }
            return this;
        }

        public static BodyBuilder BuildMessage(MimeMessage mimeMessage)
        {
            var builder = new BodyBuilder
            {
                TextBody = mimeMessage.TextBody,
                HtmlBody = mimeMessage.HtmlBody
            };
            var linkedResources = mimeMessage.BodyParts
                .Where(attachment => !attachment.IsAttachment);
            foreach (var resource in linkedResources)
                builder.LinkedResources.Add(resource);
            foreach (var attachment in mimeMessage.Attachments)
                builder.Attachments.Add(attachment);
            return builder;
        }

        public static MimePart GetMimePart(Stream stream, string fileName, string contentType = null, string contentId = null)
        {
            MimePart mimePart = null;
            if (stream != null && stream.Length > 0)
            {
                stream.Position = 0; // reset stream position ready to read
                if (string.IsNullOrWhiteSpace(contentType))
                    contentType = MimeTypes.GetMimeType(fileName);
                if (string.IsNullOrWhiteSpace(contentId))
                    contentId = MimeUtils.GenerateMessageId();
                var attachment = ContentDisposition.Attachment;
                mimePart = new MimePart(contentType)
                {
                    Content = new MimeContent(stream),
                    ContentTransferEncoding = ContentEncoding.Base64,
                    ContentDisposition = new ContentDisposition(attachment),
                    ContentId = contentId,
                    FileName = fileName ?? string.Empty,
                };
            }
            return mimePart;
        }

        public static MimePart GetMimePart(string filePath, IFileSystem fileSystem = null)
        {
            if (fileSystem == null)
                fileSystem = new FileSystem();
            MimePart mimePart = null;
            if (!string.IsNullOrWhiteSpace(filePath) && fileSystem.File.Exists(filePath))
            {
                using (var stream = fileSystem.File.OpenRead(filePath))
                {
                    string fileName = fileSystem.Path.GetFileName(filePath);
                    mimePart = GetMimePart(stream, fileName);
                }
            }
            return mimePart;
        }

        public IEmailWriter Attach(params string[] filePaths)
        {
            if (filePaths != null && filePaths.Length > 0)
            {
                var mimeEntities = new List<MimePart>();
                foreach (var filePath in filePaths)
                {
                    var mimeEntity = GetMimePart(filePath, _fileSystem);
                    if (mimeEntity != null)
                        mimeEntities.Add(mimeEntity);
                }
                Attach(mimeEntities);
            }
            return this;
        }

        public IEmailWriter Attach(Stream stream, string fileName, string contentType = null, string contentId = null, bool linkedResource = false) =>
            Attach(GetMimePart(stream, fileName, contentType, contentId), linkedResource);

        public IEmailWriter Attach(MimeEntity mimeEntity, bool linkedResource = false)
        {
            if (_mimeMessage.Body == null)
            {
                _mimeMessage.Body = mimeEntity;
            }
            else if (mimeEntity != null)
            {
                var builder = BuildMessage(_mimeMessage);
                if (!linkedResource)
                    builder.Attachments.Add(mimeEntity);
                else
                    builder.LinkedResources.Add(mimeEntity);
                _mimeMessage.Body = builder.ToMessageBody();
            }
            return this;
        }

        public IEmailWriter Attach(IEnumerable<MimeEntity> mimeEntities, bool linkedResource = false)
        {
            if (mimeEntities != null && mimeEntities.Any())
            {
                var builder = BuildMessage(_mimeMessage);
                foreach (var mimePart in mimeEntities)
                    if (!linkedResource)
                        builder.Attachments.Add(mimePart);
                    else
                        builder.LinkedResources.Add(mimePart);
                _mimeMessage.Body = builder.ToMessageBody();
            }
            return this;
        }

        public IEmailWriter TryAttach(params string[] filePaths)
        {
            if (filePaths != null)
            {
                var builder = BuildMessage(_mimeMessage);
                foreach (var filePath in filePaths)
                {
                    try
                    {
                        builder.Attachments.Add(filePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to load attachment: {filePath}");
                    }
                }
                _mimeMessage.Body = builder.ToMessageBody();
            }
            return this;
        }

        public void Send(CancellationToken cancellationToken = default, ITransferProgress transferProgress = null) =>
            SendAsync(cancellationToken, transferProgress).ConfigureAwait(false).GetAwaiter().GetResult();

        public async Task SendAsync(CancellationToken cancellationToken = default, ITransferProgress transferProgress = null)
        {
            await _emailClient.SendAsync(_mimeMessage, cancellationToken, transferProgress).ConfigureAwait(false);
            _mimeMessage = new MimeMessage();
        }

        public bool TrySend(CancellationToken cancellationToken = default, ITransferProgress transferProgress = null) =>
            TrySendAsync(cancellationToken, transferProgress).ConfigureAwait(false).GetAwaiter().GetResult();

        public async Task<bool> TrySendAsync(CancellationToken cancellationToken = default, ITransferProgress transferProgress = null)
        {
            bool isSent = await _emailClient.TrySendAsync(_mimeMessage, cancellationToken, transferProgress).ConfigureAwait(false);
            _mimeMessage = new MimeMessage();
            return isSent;
        }

        public override string ToString()
        {
            string envelope = string.Empty;
            using (var text = new StringWriter())
            {
                text.WriteLine("Date: {0}", _mimeMessage.Date);
                if (_mimeMessage.From.Count > 0)
                    text.WriteLine("From: {0}", string.Join(";", _mimeMessage.From.Mailboxes));
                if (_mimeMessage.To.Count > 0)
                    text.WriteLine("To: {0}", string.Join(";", _mimeMessage.To.Mailboxes));
                if (_mimeMessage.Cc.Count > 0)
                    text.WriteLine("Cc: {0}", string.Join(";", _mimeMessage.Cc.Mailboxes));
                if (_mimeMessage.Bcc.Count > 0)
                    text.WriteLine("Bcc: {0}", string.Join(";", _mimeMessage.Bcc.Mailboxes));
                text.WriteLine("Subject: {0}", _mimeMessage.Subject);
                text.WriteLine("Message-Id: <{0}>", _mimeMessage.MessageId);
                var attachmentCount = _mimeMessage.Attachments.Count();
                if (attachmentCount > 0)
                    text.WriteLine("{0} Attachment{1}: {2}",
                        attachmentCount, attachmentCount == 1 ? "" : "s",
                        string.Join(";", _mimeMessage.Attachments.GetAttachmentNames()));
                envelope = text.ToString();
            }
            return envelope;
        }
    }
}
