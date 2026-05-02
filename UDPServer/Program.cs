using UDPServer.Core;

namespace UDPServer;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("==== UDP 게임 서버 시작 ====");

        try
        {
            // 서버 설정 생성
            ServerConfig config = new ServerConfig();
            // UDP 게임 서버 인스턴스 생성
            UDPGameServer server = new UDPGameServer(config);
            // 서버 초기화
            server.Initialize();
            // 서버 시작
            await server.StartAsync();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[서버] 치명적 오류 발생 : {e.Message}");
            Console.WriteLine("아무키나 눌러서 종료하세요...");
            // Console.ReadKey();
        }
    }
}