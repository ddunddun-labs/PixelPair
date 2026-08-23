using System.Diagnostics;
using System.Threading;

namespace PixelPair
{
    // 캔버스 모델을 들고 에이전트 HTTP 서버(웹 UI + MCP API)를 띄우는 콘솔 호스트.
    internal static class Program
    {
        private static void Main()
        {
            var canvas = new CanvasModel();
            canvas.StatusChanged += msg => Console.WriteLine(msg);

            using var server = new AgentHttpServer(canvas);
            int port = server.Start();
            string url = $"http://127.0.0.1:{port}/";
            Console.WriteLine($"PixelPair 에이전트 HTTP: {url}  (MCP가 이 주소를 씀)");
            Console.WriteLine("종료하려면 Ctrl+C를 누르세요.");

            OpenBrowser(url);

            using var exitSignal = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                exitSignal.Set();
            };
            exitSignal.Wait();
        }

        private static void OpenBrowser(string url)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Process.Start("open", url);
                }
                else
                {
                    Process.Start("xdg-open", url);
                }
            }
            catch
            {
                // 브라우저가 없는 헤드리스 환경 등에서는 조용히 무시
            }
        }
    }
}
