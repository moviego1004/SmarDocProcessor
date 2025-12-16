using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using PdfSharp.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using PdfSharp.Fonts; 
using Windows.Media.Ocr;
using System.Text.Json; 

namespace SmartDocProcessor.Services
{
    public class AnnotationData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Type { get; set; } = "TEXT";
        public string Content { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int Page { get; set; }
        public string Color { get; set; } = "#000000";
        public int FontSize { get; set; } = 16;
    }

    public class PdfDocumentModel
    {
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "제목 없음";
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int TotalPages { get; set; } = 0;
        public int CurrentPage { get; set; } = 1;
        public double Scale { get; set; } = 1.0;
        public List<AnnotationData> Annotations { get; set; } = new List<AnnotationData>();
        public Stack<string> UndoStack { get; set; } = new Stack<string>();
        public AnnotationData? SelectedAnnotation { get; set; } = null;
    }

    public class HistoryService
    {
        private string _historyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history.json");
        private Dictionary<string, int> _history = new Dictionary<string, int>();

        public HistoryService() {
            if (File.Exists(_historyPath)) {
                try { _history = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_historyPath)) ?? new Dictionary<string, int>(); } catch { }
            }
        }
        public int GetLastPage(string filePath) => _history.ContainsKey(filePath) ? _history[filePath] : 1;
        public void SaveLastPage(string filePath, int page) {
            if (string.IsNullOrEmpty(filePath)) return;
            _history[filePath] = page;
            try { File.WriteAllText(_historyPath, JsonSerializer.Serialize(_history)); } catch { }
        }
    }

    public interface IPdfService
    {
        byte[] SavePdfWithAnnotations(byte[] originalPdf, List<AnnotationData> annotations, double currentScale);
        List<AnnotationData> ExtractAnnotationsFromMetadata(byte[] pdfBytes);
        byte[] DeletePage(byte[] pdfBytes, int pageIndex);
        byte[] AddBlankPage(byte[] pdfBytes, int insertIndex = -1); 
        byte[] AddImagePage(byte[] pdfBytes, byte[] imageBytes, int insertIndex = -1);
    }

    public class PdfService : IPdfService
    {
        public PdfService() {
            try { if (GlobalFontSettings.FontResolver == null) GlobalFontSettings.FontResolver = new SystemFontResolver(); } catch { }
        }

        public List<AnnotationData> ExtractAnnotationsFromMetadata(byte[] pdfBytes)
        {
            try {
                using (var inStream = new MemoryStream(pdfBytes)) {
                    var doc = PdfReader.Open(inStream, PdfDocumentOpenMode.Import);
                    string meta = doc.Info.Keywords;
                    if (!string.IsNullOrEmpty(meta) && meta.StartsWith("[SMARTDOC]")) {
                        string json = meta.Substring(10);
                        return JsonSerializer.Deserialize<List<AnnotationData>>(json) ?? new List<AnnotationData>();
                    }
                }
            } catch { }
            return new List<AnnotationData>();
        }

        public byte[] SavePdfWithAnnotations(byte[] originalPdf, List<AnnotationData> annotations, double currentScale)
        {
            using (var inStream = new MemoryStream(originalPdf))
            using (var outStream = new MemoryStream())
            {
                var doc = PdfReader.Open(inStream, PdfDocumentOpenMode.Modify);
                if (doc.Version < 17) doc.Version = 17;

                var editableAnns = annotations.Where(a => a.Type != "OCR_TEXT").ToList();
                doc.Info.Keywords = "[SMARTDOC]" + JsonSerializer.Serialize(editableAnns);

                var annsByPage = annotations.GroupBy(a => a.Page);

                foreach (var group in annsByPage)
                {
                    int pageIdx = group.Key - 1;
                    if (pageIdx < 0 || pageIdx >= doc.PageCount) continue;

                    var page = doc.Pages[pageIdx];
                    using (var gfx = XGraphics.FromPdfPage(page))
                    {
                        foreach (var ann in group)
                        {
                            // [수정] Unscaled 좌표를 그대로 사용
                            double x = ann.X;
                            double y = ann.Y;
                            double w = ann.Width;
                            double h = ann.Height;

                            if (ann.Type.StartsWith("HIGHLIGHT")) 
                            {
                                var color = ann.Type == "HIGHLIGHT_O" ? XColor.FromArgb(100, 255, 165, 0) : XColor.FromArgb(100, 255, 255, 0);
                                if (ann.Type.Contains("_CIR")) gfx.DrawEllipse(new XSolidBrush(color), new XRect(x, y, w, h));
                                else gfx.DrawRectangle(new XSolidBrush(color), new XRect(x, y, w, h));
                            }
                            else if (ann.Type == "UNDERLINE") 
                            {
                                var pen = new XPen(XColor.FromArgb(255, 0, 0), 2);
                                gfx.DrawLine(pen, x, y + h, x + w, y + h);
                            }
                            else if (ann.Type == "OCR_TEXT") 
                            {
                                var options = new XPdfFontOptions(PdfFontEncoding.Unicode);
                                var font = new XFont("Malgun Gothic", h * 0.65, XFontStyleEx.Regular, options);
                                var brush = new XSolidBrush(XColor.FromArgb(1, 255, 255, 255)); // 투명
                                gfx.DrawString(ann.Content, font, brush, new XRect(x, y, w, h), XStringFormats.TopLeft);
                            }
                            else if (ann.Type == "TEXT") 
                            {
                                XColor color = XColor.FromArgb(
                                    int.Parse(ann.Color.Substring(1, 2), System.Globalization.NumberStyles.HexNumber),
                                    int.Parse(ann.Color.Substring(3, 2), System.Globalization.NumberStyles.HexNumber),
                                    int.Parse(ann.Color.Substring(5, 2), System.Globalization.NumberStyles.HexNumber));
                                
                                // Font size도 Unscaled 기준 (Home.razor에서 Scaled로 보여줌)
                                double fontSize = ann.FontSize;
                                var options = new XPdfFontOptions(PdfFontEncoding.Unicode);
                                var font = new XFont("Malgun Gothic", fontSize, XFontStyleEx.Regular, options);
                                var brush = new XSolidBrush(color);
                                
                                var lines = ann.Content.Split('\n');
                                for (int i = 0; i < lines.Length; i++) {
                                    double lineX = x + 4.0;
                                    double lineY = y + 4.0 + (i * fontSize * 1.2);
                                    gfx.DrawString(lines[i], font, brush, new XPoint(lineX, lineY), XStringFormats.TopLeft);
                                }
                            }
                        }
                    }
                }
                doc.Save(outStream);
                return outStream.ToArray();
            }
        }

        public byte[] DeletePage(byte[] pdfBytes, int pageIndex) { using (var ms = new MemoryStream(pdfBytes)) using (var outMs = new MemoryStream()) { var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import); var newDoc = new PdfDocument(); for (int i = 0; i < doc.PageCount; i++) if (i != pageIndex) newDoc.AddPage(doc.Pages[i]); newDoc.Save(outMs); return outMs.ToArray(); } }
        public byte[] AddBlankPage(byte[] pdfBytes, int insertIndex = -1) { using (var ms = new MemoryStream(pdfBytes)) using (var outMs = new MemoryStream()) { var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Modify); if (insertIndex < 0 || insertIndex > doc.PageCount) doc.AddPage(); else doc.Pages.Insert(insertIndex, new PdfPage()); doc.Save(outMs); return outMs.ToArray(); } }
        public byte[] AddImagePage(byte[] pdfBytes, byte[] imageBytes, int insertIndex = -1) { using (var ms = new MemoryStream(pdfBytes)) using (var outMs = new MemoryStream()) { var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Modify); var page = new PdfPage(); if (insertIndex < 0 || insertIndex > doc.PageCount) doc.AddPage(page); else doc.Pages.Insert(insertIndex, page); using (var gfx = XGraphics.FromPdfPage(page)) using (var imgMs = new MemoryStream(imageBytes)) { var img = XImage.FromStream(imgMs); double w = img.PointWidth; double h = img.PointHeight; if (w > page.Width.Point) { h = h * (page.Width.Point / w); w = page.Width.Point; } page.Width = XUnit.FromPoint(w); page.Height = XUnit.FromPoint(h); gfx.DrawImage(img, 0, 0, w, h); } doc.Save(outMs); return outMs.ToArray(); } }
    }
}