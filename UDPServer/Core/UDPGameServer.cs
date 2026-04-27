using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using UDPServer.Models;

namespace UDPServer.Core;

public class UDPGameServer
{
    private readonly ServerConfig _config;
    private Socket _socket;
    private bool _isRunning;
    private readonly ConcurrentDictionary<int, PlayerData> _players;
    private int _nextPlayerId;
    
    private readonly JobQueue _jobQueue = new JobQueue();
    private Thread _logicThread;

    public UDPGameServer(ServerConfig config)
    {
        _config = config;
        _players = new ConcurrentDictionary<int, PlayerData>();
        _nextPlayerId = 0;
        _isRunning = false;
    }


    #region 초기화 및 시작 메서드

    // 서버 초기화
    public void Initialize()
    {
        try
        {
            // UDP 소캣 생성 (IPv4 32Bit, IPv6 128Bit)
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // 소캣옵션 설정 - 주소를 재사용 허용
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            // 소캣 바인딩 - 서버 IP와 포트로 바인딩
            IPEndPoint serverEndPoint = new IPEndPoint(IPAddress.Parse(_config.ServerIP), _config.ServerPort);
            _socket.Bind(serverEndPoint);
            
            Console.WriteLine($"[서버] 초기화 완료 : {_config.ServerIP}:{_config.ServerPort}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[서버] 초기화 실패 : {e.Message}");
            throw;
        }
    }
    
    // 서버 시작 메서드
    public async Task StartAsync()
    {
        if (_isRunning)
        {
            Console.WriteLine("[서버] 이미 실행 중입니다.");
            return;
        }
        _isRunning = true;
        Console.WriteLine("[서버] 서버 시작...");
        
        // 로직 스레드 시작
        StartLogicThread();
        
        // 비동기 수신 루프 시작
        await Task.Run(ReceiveLoop);
    }
    
    // 패킷 처리 로직 스레드
    private void StartLogicThread()
    {
        _logicThread = new Thread(() =>
        {
            Console.WriteLine("[서버] 로직 스레드 시작");
            while (_isRunning)
            {
                IJob job = _jobQueue.Dequeue();
                job.Execute();
            }

            Console.WriteLine("[서버] 로직 스레드 종료");
        });
        
        _logicThread.IsBackground = true;
        _logicThread.Start();
    }
    
    #endregion

    #region 메시지 수신 루프

    private void ReceiveLoop()
    {
        Console.WriteLine("[서버] 수신 루프 시작");
        // 패킷 수신용 버퍼
        // byte[] buffer = new byte[_config.BufferSize];
        EndPoint clientEP = new IPEndPoint(IPAddress.Any, _config.ServerPort);
        while (_isRunning)
        {
            // ArrayPool Rent
            byte[] buffer = ArrayPool<byte>.Shared.Rent(_config.BufferSize);
            try
            {
                int receiveBytes = _socket.ReceiveFrom(buffer, ref clientEP);

                if (receiveBytes > 0)
                {
                    
                    byte[] data = new byte[receiveBytes];
                    Buffer.BlockCopy(buffer, 0, data, 0, receiveBytes);
                    
                    PacketJob job = new PacketJob(
                    this,
                        data,
                        receiveBytes,
                        (IPEndPoint)clientEP
                    );
                    
                    _jobQueue.Enqueue(job);
                    Console.WriteLine($"[서버] {clientEP} 로부터 {receiveBytes} 바이트 수산");
                    
                    // // 수신된 바이트 배열을 실제 데이터 크기 만큼 복사
                    // byte[] data = new byte[receiveBytes];
                    // // 버퍼에서 실제 수신된 데이터만 복사
                    // Array.Copy(buffer, data, receiveBytes);
                    //
                    //
                    // // 수신된 데이터 처리 로직 (패킷 파싱)
                    // ProcessPacket(data, (IPEndPoint)clientEP);
                }
            }
            catch (SocketException e)
            {
                Console.WriteLine($"[서버] 소캣 오류 : {e.Message}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[서버] 수신 오류 발생 : {e.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        
        Console.WriteLine("[서버] 수신 루프 종료");
    }

    #endregion

    #region 패킷 파싱

    public void ProcessPacket(byte[] data, int bufferSize, IPEndPoint clientEP)
    {
        try
        {
            // 바이트 배열을 NetworkPacket 객체로 변환
            NetworkPacket? packet = NetworkPacket.FromBytes(data, bufferSize);

            if (packet == null)
            {
                Console.WriteLine($"[서버] 잘못된 패킷 형식 수신 from {clientEP}");
                return;
            }

            switch (packet.Type)
            {
                case PacketType.PlayerJoin:
                    Console.WriteLine("[서버] 프레이어 접속 요청 수신됨.");
                    HandlePlayerJoin(packet, clientEP);
                    break;
                case PacketType.PlayerLeave:
                    Console.WriteLine("[서버] 플레이어 접속 종료 요청 수신됨.");
                    HandlePlayerLeave(packet.PlayerId);
                    break;
                case PacketType.PlayerUpdate:
                    Console.WriteLine("[서버] 플레이어 위치 업데이트 요청 수신됨.");
                    HandlePlayerUpdate(packet, clientEP);
                    break;
                case PacketType.PlayerFire:
                    Console.WriteLine("[서버] 플레이어 발사 이벤트 수신됨.");
                    HandlePlayerFire(packet, clientEP);
                    break;
                default:
                    break;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[서버] 패킷 처리 오류 : {e.Message}");
        }
    }

    #endregion

    #region 핸들러 메서드

    // 새로운 플레이어 접속 처리
    private void HandlePlayerJoin(NetworkPacket packet, IPEndPoint clientEP)
    {
        // 최대 플레이어 수 확인
        if (_players.Count >= _config.MaxPlayers)
        {
            Console.WriteLine($"[서버] 최대 플레이어 수 초과. 접속 거부 : {clientEP}");
            return;
        }
        
        // 새로운 플레이어 ID 할당 (스레드 안전하게 증가)
        int newPlayerId = Interlocked.Increment(ref _nextPlayerId);
        
        PlayerData newPlayer = new PlayerData(newPlayerId, clientEP);
        // 초기 위치와 회전 설정
        newPlayer.UpdateTransform(packet.Position, packet.Rotation);
        
        // 플레이어 등록
        if (_players.TryAdd(newPlayerId, newPlayer))
        {
            Console.WriteLine($"[서버] 플레이어 {newPlayerId} 접속 성공 : {clientEP}");
            // 플레이어 접속 성공 응답 전송 (PacketType.PlayerSpawn)
            SendPlayerSpawn(newPlayer, clientEP);
            // 다른 플레이어들에게 새 플레이어 접속 알림 전송
            BroadcastPlayerSpawn(newPlayer);
            // 새 플레이어에게 기존 플레이어 정보 전송
            SendExistingPlayers(clientEP);
        }
        else
        {
            Console.WriteLine($"[서버] 플레이어 {newPlayerId} 등록 실패 : {clientEP}");
        }
    }
    
    // 플레이어 접속 종료 처리
    private void HandlePlayerLeave(int playerId)
    {
        if (_players.TryRemove(playerId, out PlayerData removedPlayer))
        {
            Console.WriteLine($"[서버] 플레이어 접속해제 - id: {removedPlayer.PlayerId}, EP: {removedPlayer.EndPoint}");
            
            // 다른 플레이어에게 접속 해제 알림 전송
            BroadcastPlayerDespawn(removedPlayer.PlayerId);
        }
        else
        {
            Console.WriteLine($"[서버] 플레이어 접속 해제 실패 - id: {removedPlayer.PlayerId} 존재하지 않음");
        }
    }

    private void HandlePlayerUpdate(NetworkPacket packet, IPEndPoint clientEP)
    {
        // 서버에서 해당 플레이어가 존재하는지 확인
        if (_players.TryGetValue(packet.PlayerId, out var player))
        {
            // 클라이언트 검증
            if (player.EndPoint.Equals(clientEP))
            {
                // 플레이어 위치 / 회전 정보 갱신
                player.UpdateTransform(packet.Position, packet.Rotation);
                Console.WriteLine($"[서버] 플레이어 {player.PlayerId}, Position: {packet.Position}, Rotation: {packet.Rotation} 정보 갱신 완료");
                
                // 다른 플레이어들에게 위치 업데이트 알림 발송
                BroadcastPlayerUpdate(packet, clientEP);
            }
            else
            {
                Console.WriteLine($"[서버] 플레이어 {player.PlayerId} 정보 갱신 실패 - 클라이언트 EP 불일치");
            }
        }
        else
        {
            Console.WriteLine($"[서버] 플레이어 {packet.PlayerId} 정보 갱신 실패 - 플레이어 존재하지 않음");
        }
    }

    private void HandlePlayerFire(NetworkPacket packet, IPEndPoint clientEP)
    {
        // 서버에서 해당 플레이어 정보 조회
        if (_players.TryGetValue(packet.PlayerId, out var player))
        {
            // 클라이언트 주소 검증
            if (player.EndPoint.Equals(clientEP))
            {
                Console.WriteLine($"[서버] 플레이어 {player.PlayerId} 발사 이벤트 처리 완료");
                
                // 발사 이벤트를 다른 플레이어들에게 전송
                BroadcastPlayerFire(packet, clientEP);
            }
            else
            {
                Console.WriteLine($"[서버] 플레이어 {player.PlayerId} 발사 이벤트 처리 실패 - 클라이언트 EP 불일치");
            }
        }
    }

    #endregion

    #region 패킷 전송 메서드

    // PlayerSpawn 패킷 전송
    private void SendPlayerSpawn(PlayerData player, IPEndPoint clientEP)
    {
        try
        {
            // 플레이어 스폰 패킷 생성
            NetworkPacket packet = new NetworkPacket
            {
                Type = PacketType.PlayerSpawn,
                PlayerId = player.PlayerId,
                Position = player.Position,
                Rotation = player.Rotation,
                Timestamp = DateTime.UtcNow
            };
            
            // 패킷을 바이트 배열로 변환
            byte[] data = packet.ToBytes();
            // 클라이언트에게 패킷 전송
            _socket.SendTo(data, clientEP);
            
            Console.WriteLine($"[서버] PlayerSpawn 패킷 전송 성공 to {clientEP}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[서버] PlayerSpawn 패킷 전송 오류 : {e.Message}");
        }
    }
    
    // 기존에 접속했던 Player들에게 새로운 플레이어의 생성을 알림
    private void BroadcastPlayerSpawn(PlayerData newPlayer)
    {
        int sentCount = 0;
        foreach (var existPlayer in _players.Values)
        {
            if (!existPlayer.EndPoint.Equals(newPlayer.EndPoint))
            {
                SendPlayerSpawn(newPlayer, existPlayer.EndPoint);
                sentCount++;
            }
        }

        if (sentCount > 0)
        {
            Console.WriteLine($"[서버] 기존 플레이어들에게 새로운 플레이어 {newPlayer.PlayerId} 스폰 알림 전송 완료 (총 {sentCount}명)");
        }
        else
        {
            Console.WriteLine($"[서버] 기존 플레이어가 없어 새로운 플레이어 {newPlayer.PlayerId} 스폰 알림 전송 생략");
        }
    }
    
    // 새로 접속한 플레이어에게 기존 플레이어 정보 송신
    private void SendExistingPlayers(IPEndPoint clientEP)
    {
        int sentCount = 0;
        foreach (var existPlayer in _players.Values)
        {
            if (!existPlayer.EndPoint.Equals(clientEP))
            {
                SendPlayerSpawn(existPlayer, clientEP);
                sentCount++;
            }
        }

        if (sentCount > 0)
        {
            Console.WriteLine($"[서버] 기존 플레이어 정보 {sentCount}명 전송 완료 to {clientEP} (총 {sentCount}명)");
        }
        else
        {
            Console.WriteLine($"[서버] 기존 서버 플레이어가 없어 정보 전송 생략 to {clientEP}");
        }
    }

    private void BroadcastPlayerDespawn(int removedPlayerId)
    {
        try
        {
            // 플레이어 Despawn 패킷 생성
            NetworkPacket packet = new NetworkPacket
            {
                Type = PacketType.PlayerDespawn,
                PlayerId = removedPlayerId,
                Timestamp = DateTime.UtcNow
            };
            
            byte[] data = packet.ToBytes();
            int sentCount = 0;
            foreach (var existPlayer in _players.Values)
            {
                _socket.SendTo(data, existPlayer.EndPoint);
                sentCount++;
            }
            
            Console.WriteLine($"[서버] 플레이어 {removedPlayerId} 접속 해제 알림 전송 완료 (총 {sentCount}명)");
        }
        catch (Exception e)
        {
            
            Console.WriteLine($"[서버] 플레이어 접속 해제 알림 전송 오류: {e.Message}");
        }
    }

    // 모든 플레이어에게 특정 플레이어의 위치 / 회전 값을 전송
    private void BroadcastPlayerUpdate(NetworkPacket packet, IPEndPoint clientEP)
    {
        try
        {
            NetworkPacket updatePacket = new NetworkPacket
            {
                Type = PacketType.PlayerUpdate,
                PlayerId = packet.PlayerId,
                Position = packet.Position,
                Rotation = packet.Rotation,
                Timestamp = DateTime.UtcNow
            };
            
            byte[] data = updatePacket.ToBytes();
            int sentCount = 0;
            foreach (var existPlayer in _players.Values)
            {
                if (!existPlayer.EndPoint.Equals(clientEP))
                {
                    _socket.SendTo(data, existPlayer.EndPoint);
                    sentCount++;
                }
            }
            
            Console.WriteLine($"[서버] 플레이어 {packet.PlayerId} 위치 / 회전 업데이트 전송 완료 (총 {sentCount}명)");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[서버] 플레이어 위치 / 회전 업데이트 전송 오류 : {e.Message}");
        }
    }
    
    // 발사 정보 전송
    private void BroadcastPlayerFire(NetworkPacket packet, IPEndPoint clientEP)
    {
        try
        {
            // 플레이어 발사 패킷 생성
            NetworkPacket firePacket = new NetworkPacket
            {
                Type = PacketType.PlayerFire,
                PlayerId = packet.PlayerId,
                Position = packet.Position,
                Rotation = packet.Rotation,
                Timestamp = DateTime.UtcNow
            };
            
            byte[] data = firePacket.ToBytes();
            int sentCount = 0;
            foreach (var existPlayer in _players.Values)
            {
                _socket.SendTo(data, existPlayer.EndPoint);
                sentCount++;
            }
            
            Console.WriteLine($"[서버] 플레이어 {packet.PlayerId} 발사 이벤트 전송 완료 (총 {sentCount}명)");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[서버] 플레이어 발사 이벤트 전송 오류 : {e.Message}");
        }
    }
    #endregion
}