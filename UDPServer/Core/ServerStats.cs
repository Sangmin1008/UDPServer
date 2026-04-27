using System.Timers;
using Timer = System.Timers.Timer;
namespace UDPServer.Core;

public class ServerStats : IDisposable
{
    // 접속 통계
    // 총 접속 횟수
    private int _totalJoins;
    // 동접 최고 기록
    private int _peakPlayers;

    // 패킷 통계
    // 수신 패킷
    private int _receivedPackets;
    // 송신 패킷
    private int _sentPackets;
    
    // 타이머 선언
    private readonly Timer _printTimer;
    private const float PRINT_INTERVAL = 5000f;
    
    // 접속자 수 가져오는 델리게이트
    private readonly Func<int> _getCurrentPlayerCount;

    #region 생성자

    public ServerStats(Func<int> getCurrentPlayerCount)
    {
        _getCurrentPlayerCount = getCurrentPlayerCount;
        _printTimer = new Timer(PRINT_INTERVAL);
        _printTimer.AutoReset = true;
        _printTimer.Elapsed += PrintStats;
        _printTimer.Start();
    }

    #endregion

    #region 통계 데이터 증가 메서드

    // 총 접속자 수 증가 / 최대 동접사용자 수
    public void IncrementJoins(int currentPlayerCount)
    {
        Interlocked.Increment(ref _totalJoins);
        
        // 최대 동접 사용자 갱신 : CAS(Compare And Swap) 루프
        int current = _peakPlayers;
        while (currentPlayerCount > current)
        {
            int previour = Interlocked.CompareExchange(ref _peakPlayers, currentPlayerCount, current);
            if (previour == current) break;
            current = previour;
        }
    }
    
    // 수신 패킷 증가
    public void IncrementReceivedPackets()
    {
        Interlocked.Increment(ref _receivedPackets);
    }
    
    // 송신 패킷 증가
    public void IncrementSentPackets(int count = 1)
    {
        Interlocked.Add(ref _sentPackets, count);
    }

    #endregion

    #region 통계 출력

    private void PrintStats(object? sender, ElapsedEventArgs e)
    {
        // 스냅샷 읽기
        int joins = _totalJoins; // 총 접속자 수
        int peak = _peakPlayers; // 최대 동접자 수
        int current = _getCurrentPlayerCount(); // 현재 동접자 수
        int received = _receivedPackets;
        int sent = _sentPackets;
        
        Console.WriteLine("======= 서버 통계 =======");
        Console.WriteLine("[접속]");
        Console.WriteLine($"    현재 접속자 수: \t{current, 8:N0}");
        Console.WriteLine($"    총 접속자 수: \t{joins, 8:N0}");
        Console.WriteLine($"    최대 접속자 수: \t{peak, 8:N0}");
        
        Console.WriteLine("[패킷]");
        Console.WriteLine($"    수신 패킷 수: \t{received, 8:N0}");
        Console.WriteLine($"    송신 패킷 수: \t{sent, 8:N0}");
        
        
        Console.WriteLine("========================");
    }

    #endregion

    public void Dispose()
    {
        _printTimer.Stop();
        _printTimer.Dispose();
    }
}