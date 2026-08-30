using System;

namespace S1Jarvis.Core
{
    internal enum DrDocumentSourceKind
    {
        Upload = 0,
        EmailAttachment = 10,
        CourierAttachment = 20,
        FileSystem = 30,
        Api = 40
    }

    /// <summary>
    /// Source-neutral envelope for a document entering the DR pipeline.
    /// Recognition/classification must not depend on how the bytes arrived.
    /// This allows today's curtain upload and future email attachments to use
    /// the same extraction -> historical classification -> resolution workflow.
    /// </summary>
    internal sealed class DrDocumentInput
    {
        public string DocumentKey { get; set; }
        public DrDocumentSourceKind SourceKind { get; set; }
        public string FileName { get; set; }
        public string MimeType { get; set; }
        public byte[] Content { get; set; }

        // Optional provenance. Never required by recognition itself.
        public string SourceMessageId { get; set; }
        public string SourceAttachmentId { get; set; }
        public string SourceMailbox { get; set; }
        public DateTime? ReceivedAtUtc { get; set; }

        public bool HasContent
        {
            get { return Content != null && Content.Length > 0; }
        }
    }
}
