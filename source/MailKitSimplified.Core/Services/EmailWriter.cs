﻿using System.Threading.Tasks;
using System.Threading;
using MailKitSimplified.Core.Abstractions;
using MailKitSimplified.Core.Models;

namespace MailKitSimplified.Core.Services
{
    public class EmailWriter : IEmailWriter
    {
        public IEmail Email => _email;
        private readonly IEmail _email;

        private EmailWriter(IEmail email)
        {
            _email = email;
        }

        public static EmailWriter CreateFrom(IEmail email) => new EmailWriter(email);

        public IEmailWriter From(string emailAddress, string name = "")
        {
            _email.From = new EmailContact(emailAddress, name);
            return this;
        }

        public IEmailWriter To(string emailAddress, string name = "")
        {
            _email.To.Add(new EmailContact(emailAddress, name));
            return this;
        }

        public IEmailWriter Subject(string subject)
        {
            _email.Subject = subject ?? string.Empty;
            return this;
        }

        public IEmailWriter Body(string body, bool isHtml)
        {
            _email.Body = body ?? string.Empty;
            _email.IsHtml = isHtml;
            return this;
        }

        public IEmailWriter Attach(params string[] filePaths)
        {
            if (filePaths != null)
                foreach (var filePath in filePaths)
                    if (!string.IsNullOrWhiteSpace(filePath))
                        _email.AttachmentFilePaths.Add(filePath);
            return this;
        }

        public async Task SendAsync(CancellationToken cancellationToken = default) =>
            await _email.SendAsync(cancellationToken).ConfigureAwait(false);

        public async Task<bool> TrySendAsync(CancellationToken cancellationToken = default) =>
            await _email.TrySendAsync(cancellationToken).ConfigureAwait(false);
    }
}