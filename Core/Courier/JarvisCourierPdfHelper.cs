using System.Collections.Generic;
using System.IO;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace S1Jarvis.Core.Courier
{
    // Jarvis-owned PDF helper used by courier providers that may return
    // one PDF per parcel. Keeping it in Jarvis removes the need to call
    // S1Courier.Core.PdfHelper once the standalone assembly is detached.
    internal static class JarvisCourierPdfHelper
    {
        public static byte[] MergePdfs(List<byte[]> pdfs)
        {
            if (pdfs == null || pdfs.Count == 0)
                return null;

            if (pdfs.Count == 1)
                return pdfs[0];

            using (var outputDocument = new PdfDocument())
            {
                foreach (var pdfBytes in pdfs)
                {
                    if (pdfBytes == null || pdfBytes.Length == 0)
                        continue;

                    using (var stream = new MemoryStream(pdfBytes))
                    using (var inputDocument = PdfReader.Open(stream, PdfDocumentOpenMode.Import))
                    {
                        for (int i = 0; i < inputDocument.PageCount; i++)
                            outputDocument.AddPage(inputDocument.Pages[i]);
                    }
                }

                using (var output = new MemoryStream())
                {
                    outputDocument.Save(output, false);
                    return output.ToArray();
                }
            }
        }
    }
}
