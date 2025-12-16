using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SmartDocProcessor.Services;

namespace SmartDocProcessor
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            var serviceCollection = new ServiceCollection();
            serviceCollection.AddWpfBlazorWebView();
            
            // F12 개발자 도구 활성화 (항상)
            serviceCollection.AddBlazorWebViewDeveloperTools();

            // 서비스 등록
            serviceCollection.AddSingleton<IPdfService, PdfService>();
            serviceCollection.AddSingleton<IOcrService, OcrService>();
            serviceCollection.AddSingleton<HistoryService>(); 

            // [중요 수정] "Services" 대문자! (XAML에서 {DynamicResource Services}로 찾음)
            Resources.Add("Services", serviceCollection.BuildServiceProvider());
        }
    }
}