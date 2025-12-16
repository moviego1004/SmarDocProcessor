using System;
using System.IO;
using PdfSharp.Fonts;

namespace SmartDocProcessor.Services
{
    // 폰트 파일(.ttf)의 물리적 위치를 찾아주는 역할
    public class SystemFontResolver : IFontResolver
    {
        public string DefaultFontName => "Arial";

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            // "Malgun Gothic"을 요청하면 내부적으로 "Malgun"이라는 이름으로 식별함
            if (familyName.Equals("Malgun Gothic", StringComparison.CurrentCultureIgnoreCase))
            {
                if (isBold) return new FontResolverInfo("MalgunBold");
                return new FontResolverInfo("Malgun");
            }

            // 그 외에는 기본 Arial로 처리
            return new FontResolverInfo("Arial");
        }

        public byte[] GetFont(string faceName)
        {
            string fontFile = "";

            switch (faceName)
            {
                case "Malgun":
                    fontFile = @"C:\Windows\Fonts\malgun.ttf"; // 맑은 고딕 일반
                    break;
                case "MalgunBold":
                    fontFile = @"C:\Windows\Fonts\malgunbd.ttf"; // 맑은 고딕 굵게
                    break;
                case "Arial":
                default:
                    fontFile = @"C:\Windows\Fonts\arial.ttf"; // 기본 Arial
                    break;
            }

            if (File.Exists(fontFile))
            {
                return File.ReadAllBytes(fontFile);
            }

            return new byte[0]; // 폰트 파일 없으면 빈 값 (에러 방지)
        }
    }
}