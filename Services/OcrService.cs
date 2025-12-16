using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media.Imaging; 
using Windows.Globalization;
using Windows.Graphics.Imaging; 
using Windows.Media.Ocr;
using System.Linq; // Linq 추가

namespace SmartDocProcessor.Services
{
    public class OcrResultData
    {
        public string Text { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public interface IOcrService
    {
        Task<string> ExtractTextFromImage(byte[] imageBytes);
        Task<List<OcrResultData>> ExtractTextWithCoords(byte[] imageBytes);
        string GetCurrentLanguage(); // [NEW] 현재 언어 확인용
    }

    public class OcrService : IOcrService
    {
        private OcrEngine? _ocrEngine;

        public OcrService() {
            // 1순위: 한국어 시도
            TryInitOcr("ko-KR");
            // 2순위: 영어 시도
            if (_ocrEngine == null) TryInitOcr("en-US");
            // 3순위: 시스템 기본값
            if (_ocrEngine == null) _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
        }

        private void TryInitOcr(string langCode) {
            if (_ocrEngine != null) return;
            try {
                var lang = new Language(langCode);
                if (OcrEngine.IsLanguageSupported(lang)) _ocrEngine = OcrEngine.TryCreateFromLanguage(lang);
            } catch { }
        }

        public string GetCurrentLanguage()
        {
            return _ocrEngine?.RecognizerLanguage.DisplayName ?? "OCR 엔진 없음 (Windows 설정에서 언어팩 확인 필요)";
        }

        public async Task<string> ExtractTextFromImage(byte[] imageBytes) {
            var results = await ExtractTextWithCoords(imageBytes);
            return string.Join(" ", results.Select(r => r.Text));
        }

        public async Task<List<OcrResultData>> ExtractTextWithCoords(byte[] imageBytes)
        {
            var list = new List<OcrResultData>();
            if (_ocrEngine == null) return list;

            try {
                using (var ms = new MemoryStream(imageBytes)) {
                    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
                    
                    // [중요] 이미지가 너무 크면 OCR이 실패할 수 있으므로 리사이징 고려 가능하지만,
                    // 일단 고화질 인식을 위해 원본 그대로 넘김.
                    using (var softwareBitmap = await decoder.GetSoftwareBitmapAsync()) {
                        var result = await _ocrEngine.RecognizeAsync(softwareBitmap);
                        
                        foreach (var line in result.Lines) {
                            foreach (var word in line.Words) {
                                list.Add(new OcrResultData {
                                    Text = word.Text,
                                    X = word.BoundingRect.X,
                                    Y = word.BoundingRect.Y,
                                    Width = word.BoundingRect.Width,
                                    Height = word.BoundingRect.Height
                                });
                            }
                        }
                    }
                }
            } catch { }
            return list;
        }
    }
}