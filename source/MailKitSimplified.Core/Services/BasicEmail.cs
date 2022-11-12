﻿using System.IO;
using System.Collections.Generic;
using MailKitSimplified.Core.Abstractions;

namespace MailKitSimplified.Core.Services
{
    public class BasicEmail : IBasicEmail
    {
        public IList<IEmailContact> From { get; set; } = new List<IEmailContact>();

        public IList<IEmailContact> To { get; set; } = new List<IEmailContact>();

        public IList<IEmailContact> Cc { get; set; } = new List<IEmailContact>();

        public IList<IEmailContact> Bcc { get; set; } = new List<IEmailContact>();

        public string Subject { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;

        public bool IsHtml { get; set; } = false;

        public static IEmailWriter Write => new EmailWriter();

        public override string ToString()
        {
            string envelope = string.Empty;
            using (var text = new StringWriter())
            {
                text.WriteLine("From: {0}", string.Join(";", From));
                if (To.Count > 0)
                    text.WriteLine("To: {0}", string.Join(";", To));
                if (Cc.Count > 0)
                    text.WriteLine("Cc: {0}", string.Join(";", Cc));
                if (Bcc.Count > 0)
                    text.WriteLine("Bcc: {0}", string.Join(";", Bcc));
                text.WriteLine("Subject: {0}", Subject);
                envelope = text.ToString();
            }
            return envelope;
        }
    }
}