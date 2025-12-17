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
using PdfSharp.Pdf.Annotations; 
using PdfSharp.Pdf.AcroForms;

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

    public class PdfGenericAnnotation : PdfAnnotation
    {
        public PdfGenericAnnotation(PdfDocument document) : base(document)
        {
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
                
                var editableAnns = annotations.Where(a => a.Type != "OCR_TEXT").ToList();
                doc.Info.Keywords = "[SMARTDOC]" + JsonSerializer.Serialize(editableAnns);

                // [수정] AcroForm을 직접 생성하지 않고 접근합니다. (PdfSharp이 자동 생성함)
                // NeedAppearances: 외부 뷰어에게 주석 모양을 자동으로 그려달라고 요청하는 플래그
                try {
                    if (doc.AcroForm.Elements.ContainsKey("/NeedAppearances"))
                    {
                        doc.AcroForm.Elements["/NeedAppearances"] = new PdfBoolean(true);
                    }
                    else
                    {
                        doc.AcroForm.Elements.Add("/NeedAppearances", new PdfBoolean(true));
                    }
                }
                catch { 
                    // 혹시 AcroForm 접근이 실패해도 진행 (필수는 아님)
                }

                var annsByPage = annotations.GroupBy(a => a.Page);

                // 기존 주석 초기화
                foreach (var page in doc.Pages) 
                {
                    if(page.Annotations != null) page.Annotations.Clear();
                }

                foreach (var group in annsByPage)
                {
                    int pageIdx = group.Key - 1;
                    if (pageIdx < 0 || pageIdx >= doc.PageCount) continue;

                    var page = doc.Pages[pageIdx];
                    double pageHeight = page.Height.Point;

                    var ocrAnns = group.Where(a => a.Type == "OCR_TEXT").ToList();
                    if (ocrAnns.Any())
                    {
                        using (var gfx = XGraphics.FromPdfPage(page))
                        {
                            foreach (var ann in ocrAnns)
                            {
                                var options = new XPdfFontOptions(PdfFontEncoding.Unicode);
                                var font = new XFont("Malgun Gothic", ann.Height * 0.65, XFontStyleEx.Regular, options);
                                var brush = new XSolidBrush(XColor.FromArgb(1, 255, 255, 255));
                                gfx.DrawString(ann.Content, font, brush, new XRect(ann.X, ann.Y, ann.Width, ann.Height), XStringFormats.TopLeft);
                            }
                        }
                    }

                    foreach (var ann in group.Where(a => a.Type != "OCR_TEXT"))
                    {
                        var rect = new PdfRectangle(new XRect(ann.X, pageHeight - ann.Y - ann.Height, ann.Width, ann.Height));

                        if (ann.Type.StartsWith("HIGHLIGHT"))
                        {
                            var annot = new PdfGenericAnnotation(doc);
                            annot.Elements.SetName(PdfAnnotation.Keys.Type, "/Annot");
                            annot.Elements.SetName(PdfAnnotation.Keys.Subtype, "/Highlight");
                            annot.Elements.SetRectangle(PdfAnnotation.Keys.Rect, rect);
                            
                            var color = ann.Type == "HIGHLIGHT_O" ? XColor.FromArgb(255, 165, 0) : XColor.FromArgb(255, 255, 0);
                            annot.Elements["/C"] = new PdfArray(doc, new PdfReal(color.R / 255.0), new PdfReal(color.G / 255.0), new PdfReal(color.B / 255.0));
                            
                            // QuadPoints 설정 (외부 뷰어 가시성 핵심)
                            var qp = new PdfArray(doc);
                            qp.Elements.Add(new PdfReal(rect.X1)); qp.Elements.Add(new PdfReal(rect.Y2)); // Top-Left
                            qp.Elements.Add(new PdfReal(rect.X2)); qp.Elements.Add(new PdfReal(rect.Y2)); // Top-Right
                            qp.Elements.Add(new PdfReal(rect.X1)); qp.Elements.Add(new PdfReal(rect.Y1)); // Bottom-Left
                            qp.Elements.Add(new PdfReal(rect.X2)); qp.Elements.Add(new PdfReal(rect.Y1)); // Bottom-Right
                            annot.Elements["/QuadPoints"] = qp;

                            page.Annotations.Add(annot);
                        }
                        else if (ann.Type == "UNDERLINE")
                        {
                            var annot = new PdfGenericAnnotation(doc);
                            annot.Elements.SetName(PdfAnnotation.Keys.Type, "/Annot");
                            annot.Elements.SetName(PdfAnnotation.Keys.Subtype, "/Underline");
                            annot.Elements.SetRectangle(PdfAnnotation.Keys.Rect, rect);
                            annot.Elements["/C"] = new PdfArray(doc, new PdfReal(1), new PdfReal(0), new PdfReal(0)); // Red
                            
                            var qp = new PdfArray(doc);
                            qp.Elements.Add(new PdfReal(rect.X1)); qp.Elements.Add(new PdfReal(rect.Y2));
                            qp.Elements.Add(new PdfReal(rect.X2)); qp.Elements.Add(new PdfReal(rect.Y2));
                            qp.Elements.Add(new PdfReal(rect.X1)); qp.Elements.Add(new PdfReal(rect.Y1));
                            qp.Elements.Add(new PdfReal(rect.X2)); qp.Elements.Add(new PdfReal(rect.Y1));
                            annot.Elements["/QuadPoints"] = qp;

                            page.Annotations.Add(annot);
                        }
                        else if (ann.Type == "TEXT")
                        {
                            var annot = new PdfGenericAnnotation(doc);
                            annot.Elements.SetName(PdfAnnotation.Keys.Type, "/Annot");
                            annot.Elements.SetName(PdfAnnotation.Keys.Subtype, "/FreeText");
                            annot.Elements.SetRectangle(PdfAnnotation.Keys.Rect, rect);
                            annot.Elements.SetString("/Contents", ann.Content);
                            
                            // 색상 변환
                            XColor c = XColor.FromArgb(
                                int.Parse(ann.Color.Substring(1, 2), System.Globalization.NumberStyles.HexNumber),
                                int.Parse(ann.Color.Substring(3, 2), System.Globalization.NumberStyles.HexNumber),
                                int.Parse(ann.Color.Substring(5, 2), System.Globalization.NumberStyles.HexNumber));
                            
                            double r = c.R / 255.0; double g = c.G / 255.0; double b = c.B / 255.0;
                            // Default Appearance (/DA) 설정: 외부 뷰어가 이 정보를 보고 텍스트를 그립니다.
                            annot.Elements["/DA"] = new PdfString($"/Helv {ann.FontSize} Tf {r:0.##} {g:0.##} {b:0.##} rg");
                            
                            page.Annotations.Add(annot);
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