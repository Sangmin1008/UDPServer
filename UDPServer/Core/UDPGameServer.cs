using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using UDPServer.Models;

namespace UDPServer.Core;

public class UDPGameServer : IDisposable
{
    private readonly ServerConfig _config;
    private Socket _socket;
    private bool _isRunning;
    private readonly ConcurrentDictionary<int, PlayerData> _players;
    private int _nextPlayerId;
    private TimeoutManager _timeoutManager;

    private long _nextSequence = 1;
    private readonly ConcurrentDictionary<uint, PendingPacket> _pendingPackets;
    private Timer _watchdogTimer;
    private readonly ConcurrentDictionary<string, byte> _processedReliablePackets = new();
    private readonly object _joinLock = new();
    private readonly ConcurrentDictionary<string, int> _endpointToPlayerId = new();
    
    
    private int _nextItemId = 0;
    private readonly ConcurrentDictionary<int, NetworkPacket> _activeItems;
    private CancellationTokenSource? _itemSpawnCts;
    
    // 서버 통계 수집
    private readonly ServerStats _stats;
    
    
    // JobQueue 및 패킷 스레드
    private readonly JobQueue _jobQueue = new JobQueue();
    
    // 멀티스레드 패킷 처리
    private Thread[] _workerThreads;
    private const int WORKER_THREAD_COUNT = 10;

    public UDPGameServer(ServerConfig config)
    {
        _config = config;
        _players = new ConcurrentDictionary<int, PlayerData>();
        _nextPlayerId = 0;
        _isRunning = false;
        _stats = new ServerStats(() => _players.Count);
        _activeItems = new ConcurrentDictionary<int, NetworkPacket>();
        _pendingPackets = new ConcurrentDictionary<uint, PendingPacket>();
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
            
            // TimeoutManager 초기화
            _timeoutManager = new TimeoutManager(_players, _config.PlayerTimeoutSeconds, HandlePlayerTimeout);
            
            _watchdogTimer = new Timer(CheckPendingPackets, null, 100, 100);
            
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
        
        _itemSpawnCts = new CancellationTokenSource();
        _ = ItemSpawnLoopAsync(_itemSpawnCts.Token);
        
        // 비동기 수신 루프 시작
        await Task.Run(ReceiveLoop);
    }
    
    // 패킷 처리 로직 스레드
    private void StartLogicThread()
    {
        _workerThreads = new Thread[WORKER_THREAD_COUNT];

        for (int i = 0; i < WORKER_THREAD_COUNT; i++)
        {
            int threadId = i;
            _workerThreads[i] = new Thread(() => WorkerThreadLoop(threadId));
            _workerThreads[i].IsBackground = true;
            _workerThreads[i].Start();
        }
        
        Console.WriteLine($"[서버] 패킷 처리용 워커 스레드 {WORKER_THREAD_COUNT}개 시작");
    }

    private void WorkerThreadLoop(int threadId)
    {
        int processCount = 0;
        while (_isRunning)
        {
            try
            {
                IJob job = _jobQueue.Dequeue();
                job.Execute();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[서버] 워커 스레드 {threadId} 패킷 처리 완료 (총: {++processCount})");
                Console.ResetColor();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[서버] 워커 스레드 {threadId} 실행 오류 : {e.Message}");
            }
        }
    }
    
    #endregion

    #region 키 헬퍼

    private static string GetEndpointKey(IPEndPoint ep)
    {
        return $"{ep.Address}:{ep.Port}";
    }

    private static string GetReliableKey(IPEndPoint ep, uint sequence)
    {
        return $"{ep.Address}:{ep.Port}:{sequence}";
    }

    #endregion

    #region 메시지 수신 루프

    private void ReceiveLoop()
    {
        Console.WriteLine("[서버] 수신 루프 시작");

        while (_isRunning)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(_config.BufferSize);

            try
            {
                // ReceiveFrom이 채워 넣을 임시 endpoint
                EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

                int receiveBytes = _socket.ReceiveFrom(buffer, ref remoteEP);

                if (receiveBytes <= 0)
                    continue;

                _stats.IncrementReceivedPackets();

                // 실제 받은 크기만큼 복사
                byte[] data = new byte[receiveBytes];
                Buffer.BlockCopy(buffer, 0, data, 0, receiveBytes);

                // 중요: remoteEP는 다음 ReceiveFrom에서 다시 바뀌므로
                // Job에는 반드시 값 복사본을 넣는다.
                IPEndPoint ep = (IPEndPoint)remoteEP;
                IPEndPoint endpointCopy = new IPEndPoint(ep.Address, ep.Port);

                PacketJob job = new PacketJob(
                    this,
                    data,
                    receiveBytes,
                    endpointCopy
                );

                _jobQueue.Enqueue(job);

                Console.WriteLine($"[서버] {endpointCopy} 로부터 {receiveBytes} 바이트 수신");
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

    #region 아이템 스폰 로직

    private async Task ItemSpawnLoopAsync(CancellationToken token)
    {
        Console.WriteLine("[서버] 아이템 스폰 루프 가동 시작");
        Random random = new Random();

        try
        {
            while (_isRunning && !token.IsCancellationRequested)
            {
                // 10초 ~ 15초 사이의 무작위 대기 시간
                int waitTime = random.Next(10000, 15000);
                await Task.Delay(waitTime, token);

                // 💡 접속한 플레이어가 2명 이상일 때만 스폰
                if (_players.Count >= 2)
                {
                    SpawnRandomItem(random);
                }
            }
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("[서버] 아이템 스폰 루프가 안전하게 종료되었습니다.");
        }
    }

    private void SpawnRandomItem(Random random)
    {
        int itemId = Interlocked.Increment(ref _nextItemId);
        int itemType = random.Next(1, 4); 

        float spawnX = (random.NextSingle() * 40f) - 20f;
        float spawnZ = (random.NextSingle() * 40f) - 20f;
        Vector3 spawnPos = new Vector3(spawnX, 15f, spawnZ);

        NetworkPacket itemPacket = new NetworkPacket
        {
            Type = PacketType.ItemSpawn,
            ItemId = itemId,
            ItemType = itemType,
            Position = spawnPos,
            Timestamp = DateTime.UtcNow
        };

        _activeItems.TryAdd(itemId, itemPacket);

        byte[] data = itemPacket.ToBytes();
        int sentCount = 0;
        foreach (var player in _players.Values)
        {
            _socket.SendTo(data, player.EndPoint);
            sentCount++;
        }
        
        _stats.IncrementSentPackets(sentCount);
        Console.WriteLine($"[서버] 아이템 스폰 (ID:{itemId}, 타입:{itemType}, 인원:{_players.Count}명)");
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
            
            // 모든 패킷 수신시 HeartBeat 처리
            if (packet.PlayerId > 0 && _players.TryGetValue(packet.PlayerId, out var player))
            {
                player.RefreshLastUpdateTime();
            }
            
            // 클라이언트가 패킷을 잘 받았다고 패킷을 보냄
            if (packet.Type == PacketType.Ack)
            {
                if (_pendingPackets.TryRemove(packet.Sequence, out _))
                {
                    Console.WriteLine($"[서버] 패킷 {packet.Sequence} 수신 확인 완료.");
                }
                return;
            }

            // 클라이언트가 보낸 중요한 패킷(Hit, Item 등)을 서버가 받은 경우 -> 서버도 Ack를 답장해줌
            if (packet.IsReliable)
            {
                string reliableKey = GetReliableKey(clientEP, packet.Sequence);

                if (!_processedReliablePackets.TryAdd(reliableKey, 0))
                {
                    SendAck(packet.Sequence, clientEP);
                    return;
                }

                SendAck(packet.Sequence, clientEP);
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
                case PacketType.PlayerHit:
                    Console.WriteLine("[서버] 플레이어 피격 이벤트 수신됨.");
                    HandlePlayerHit(packet, clientEP);
                    break;
                case PacketType.Heartbeat:
                    Console.WriteLine("[서버] 하트비트 패킷 수신됨.");
                    HandleHeartBeat(packet, clientEP);
                    break;
                case PacketType.ItemPickup:
                    Console.WriteLine("[서버] 아이템 획득 요청 수신됨.");
                    HandleItemPickup(packet, clientEP);
                    break;
                case PacketType.PlayerEmoticon:
                    Console.WriteLine("[서버] 플레이어 이모티콘 이벤트 수신됨.");
                    HandleEmoticon(packet, clientEP);
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
    
    private void SendAck(uint receivedSequence, IPEndPoint clientEP)
    {
        NetworkPacket ackPacket = new NetworkPacket
        {
            Type = PacketType.Ack,
            Sequence = receivedSequence
        };

        _socket.SendTo(ackPacket.ToBytes(), clientEP);
    }
    


    #endregion

    #region 재전송 타이머

    private void CheckPendingPackets(object? state)
    {
        if (!_isRunning) return;

        DateTime now = DateTime.UtcNow;

        foreach (var kvp in _pendingPackets)
        {
            var pending = kvp.Value;
            
            // 보낸 지 200ms가 넘었는데 아직 안 지워졌다면 유실 간주
            if ((now - pending.LastSentTime).TotalMilliseconds > 200)
            {
                if (pending.RetryCount < 5) // 최대 5회 재전송
                {
                    pending.RetryCount++;
                    pending.LastSentTime = now;
                    
                    // 다시 전송
                    byte[] data = pending.Packet.ToBytes();
                    _socket.SendTo(data, pending.TargetEndPoint);
                    Console.WriteLine($"[서버] 패킷 {kvp.Key}({pending.Packet.Type}) 재전송 시도 {pending.RetryCount}회");
                }
                else
                {
                    // 5회 모두 실패하면 포기하고 장부에서 삭제
                    Console.WriteLine($"[서버] 패킷 {kvp.Key} 최종 전송 실패. 타임아웃 처리");
                    _pendingPackets.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    #endregion

    #region 핸들러 메서드

    // 하트비트 패킷 처리
    private void HandleHeartBeat(NetworkPacket packet, IPEndPoint clientEP)
    {
        if (_players.TryGetValue(packet.PlayerId, out var player))
        {
            // 클라이언트 주소 검증
            if (player.EndPoint.Equals(clientEP))
            {
                player.RefreshLastUpdateTime();
            }
        }
    }

    // 새로운 플레이어 접속 처리
    private void HandlePlayerJoin(NetworkPacket packet, IPEndPoint clientEP)
    {
        string endpointKey = GetEndpointKey(clientEP);
        int assignedPlayerId;

        lock (_joinLock)
        {
            if (_endpointToPlayerId.TryGetValue(endpointKey, out assignedPlayerId))
            {
                // 이미 접속한 유저의 재전송된 Join 패킷이면 무시
                Console.WriteLine($"[서버] 중복 Join 요청 무시 (이미 ID {assignedPlayerId} 발급됨) : {clientEP}");
                return; 
            }

            if (_players.Count >= _config.MaxPlayers)
            {
                Console.WriteLine($"[서버] 최대 플레이어 수 초과. 접속 거부 : {clientEP}");
                return;
            }

            assignedPlayerId = Interlocked.Increment(ref _nextPlayerId);
            PlayerData newPlayer = new PlayerData(assignedPlayerId, clientEP);
            newPlayer.UpdateTransform(packet.Position, packet.Rotation);

            _players.TryAdd(assignedPlayerId, newPlayer);

            _endpointToPlayerId[endpointKey] = assignedPlayerId;

        }
        Console.WriteLine($"[서버] 플레이어 {assignedPlayerId} 접속 성공 : {clientEP}");

        // 락 밖에서 전송
        _stats.IncrementJoins(_players.Count);
        
        SendJoinSuccess(assignedPlayerId, clientEP);

        var player = _players[assignedPlayerId];
        SendPlayerSpawn(player, clientEP);
        BroadcastPlayerSpawn(player);
        SendExistingPlayers(clientEP);
    }
    
    // 플레이어 접속 종료 처리
    private void HandlePlayerLeave(int playerId)
    {
        if (_players.TryRemove(playerId, out PlayerData removedPlayer))
        {
            _endpointToPlayerId.TryRemove(GetEndpointKey(removedPlayer.EndPoint), out _);
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

    private void HandlePlayerTimeout(int playerId)
    {
        Console.WriteLine($"[서버] 플레이어 {playerId} 타임아웃 처리 중...");

        if (_players.TryGetValue(playerId, out var player))
        {
            SendPlayerTimeout(playerId, player.EndPoint);
        }
        
        // 플레이어 정보 제거
        HandlePlayerLeave(playerId);
    }

    private void HandlePlayerHit(NetworkPacket packet, IPEndPoint clientEP)
    {
        // 서버에서 해당 공격자(쏜 사람) 정보 조회
        if (_players.TryGetValue(packet.PlayerId, out var player))
        {
            // 클라이언트 주소 검증
            if (player.EndPoint.Equals(clientEP))
            {
                Console.WriteLine($"[서버] 플레이어 {packet.PlayerId}가 플레이어 {packet.TargetId}를 명중! (데미지: {packet.Damage})");
                
                // 피격 이벤트를 모든 플레이어에게 브로드캐스트
                BroadcastPlayerHit(packet, clientEP);
            }
            else
            {
                Console.WriteLine($"[서버] 플레이어 {packet.PlayerId} 피격 이벤트 처리 실패 - 클라이언트 EP 불일치");
            }
        }
    }
    
    private void HandleItemPickup(NetworkPacket packet, IPEndPoint clientEP)
    {
        // 요청을 보낸 플레이어가 유효한지 검증
        if (_players.TryGetValue(packet.PlayerId, out var player))
        {
            if (player.EndPoint.Equals(clientEP))
            {
                // [서버 판정] 아이템이 아직 맵에 존재하는가?
                if (_activeItems.TryRemove(packet.ItemId, out var itemPacket))
                {
                    Console.WriteLine($"[서버] 플레이어 {packet.PlayerId}가 아이템 {packet.ItemId}(타입:{itemPacket.ItemType}) 획득 성공!");
                    
                    // 모두에게 아이템이 소비되었음을 알림
                    BroadcastItemConsumed(packet.ItemId, packet.PlayerId, itemPacket.ItemType);
                }
                else
                {
                    // 0.1초 차이로 다른 플레이어가 먼저 먹었거나, 없는 아이템인 경우
                    Console.WriteLine($"[서버] 플레이어 {packet.PlayerId}의 아이템 {packet.ItemId} 획득 실패 (이미 소진됨)");
                }
            }
        }
    }

    private void HandleEmoticon(NetworkPacket packet, IPEndPoint clientEP)
    {
        if (_players.TryGetValue(packet.PlayerId, out var player))
        {
            if (player.EndPoint.Equals(clientEP))
            {
                Console.WriteLine($"[서버] 플레이어 {player.PlayerId} 이모티콘 이벤트 처리 완료");
                BroadcastPlayerEmoticon(packet.EmoticonId, packet.PlayerId);
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

    

    #endregion

    #region 패킷 전송 메서드
    private void SendJoinSuccess(int playerId, IPEndPoint clientEP)
    {
        NetworkPacket packet = new NetworkPacket
        {
            Type = PacketType.JoinSuccess,
            PlayerId = playerId,
            Timestamp = DateTime.UtcNow
        };
        
        SendReliable(packet, clientEP); 
    }
    
    private NetworkPacket ClonePacket(NetworkPacket src)
    {
        return new NetworkPacket
        {
            Type = src.Type,
            PlayerId = src.PlayerId,
            TargetId = src.TargetId,
            ItemId = src.ItemId,
            ItemType = src.ItemType,
            EmoticonId = src.EmoticonId,
            Damage = src.Damage,
            Position = src.Position,
            Rotation = src.Rotation,
            Timestamp = src.Timestamp,
            Sequence = src.Sequence,
            IsReliable = src.IsReliable
        };
    }
    
    // 중요 데이터 전송 전용 메서드
    private void SendReliable(NetworkPacket packet, IPEndPoint clientEP)
    {
        NetworkPacket outgoing = ClonePacket(packet);

        uint seq = (uint)Interlocked.Increment(ref _nextSequence);
        outgoing.Sequence = seq;
        outgoing.IsReliable = true;

        _pendingPackets[seq] = new PendingPacket
        {
            Packet = outgoing,
            TargetEndPoint = clientEP,
            LastSentTime = DateTime.UtcNow,
            RetryCount = 0
        };

        byte[] data = outgoing.ToBytes();
        _socket.SendTo(data, clientEP);
    }
    
    // 타임아웃 패킷 전송
    private void SendPlayerTimeout(int playerId, IPEndPoint playerEndPoint)
    {
        try
        {
            NetworkPacket timeoutPacket = new NetworkPacket
            {
                Type = PacketType.Timeout,
                PlayerId = playerId,
            };

            byte[] data = timeoutPacket.ToBytes();
            _socket.SendTo(data, playerEndPoint);
            
            _stats.IncrementSentPackets();
            Console.WriteLine($"[서버] 플레이어 {playerId} 타임아웃 패킷 전송 완료 to {playerEndPoint}");
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }
    }

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
            // byte[] data = packet.ToBytes();
            // 클라이언트에게 패킷 전송
            // _socket.SendTo(data, clientEP);
            
            SendReliable(packet, clientEP);
            _stats.IncrementSentPackets();
            
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
            
            // byte[] data = packet.ToBytes();
            int sentCount = 0;
            foreach (var existPlayer in _players.Values)
            {
                // _socket.SendTo(data, existPlayer.EndPoint);
                SendReliable(packet, existPlayer.EndPoint);
                sentCount++;
            }
            _stats.IncrementSentPackets(sentCount);
            
            // 플레이어 정보 제거
            _players.TryRemove(removedPlayerId, out _);
            
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
            
            _stats.IncrementSentPackets(sentCount);
            
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
            
            // byte[] data = firePacket.ToBytes();
            int sentCount = 0;
            foreach (var existPlayer in _players.Values)
            {
                // _socket.SendTo(data, existPlayer.EndPoint);
                SendReliable(firePacket, existPlayer.EndPoint);
                sentCount++;
            }
            
            Console.WriteLine($"[서버] 플레이어 {packet.PlayerId} 발사 이벤트 전송 완료 (총 {sentCount}명)");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[서버] 플레이어 발사 이벤트 전송 오류 : {e.Message}");
        }
    }
    
    private void BroadcastPlayerHit(NetworkPacket packet, IPEndPoint clientEP)
    {
        try
        {
            // 피격 에코 패킷 생성
            NetworkPacket hitPacket = new NetworkPacket
            {
                Type = PacketType.PlayerHit,
                PlayerId = packet.PlayerId, // 쏜 사람
                TargetId = packet.TargetId, // 맞은 사람
                Damage = packet.Damage,     // 데미지
                Timestamp = DateTime.UtcNow
            };
            
            // byte[] data = hitPacket.ToBytes();
            int sentCount = 0;
            
            foreach (var existPlayer in _players.Values)
            {
                // _socket.SendTo(data, existPlayer.EndPoint);
                SendReliable(hitPacket, existPlayer.EndPoint);
                sentCount++;
            }
            
            _stats.IncrementSentPackets(sentCount);
            Console.WriteLine($"[서버] 플레이어 피격 이벤트 전송 완료 (총 {sentCount}명)");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[서버] 플레이어 피격 이벤트 전송 오류 : {e.Message}");
        }
    }
    
    private void BroadcastItemConsumed(int itemId, int playerId, int itemType)
    {
        try
        {
            NetworkPacket consumedPacket = new NetworkPacket
            {
                Type = PacketType.ItemConsumed,
                ItemId = itemId,
                PlayerId = playerId,
                ItemType = itemType,
                Timestamp = DateTime.UtcNow
            };

            // byte[] data = consumedPacket.ToBytes();
            int sentCount = 0;
            
            foreach (var existPlayer in _players.Values)
            {
                // _socket.SendTo(data, existPlayer.EndPoint);
                SendReliable(consumedPacket, existPlayer.EndPoint);
                sentCount++;
            }
            
            _stats.IncrementSentPackets(sentCount);
            Console.WriteLine($"[서버] 아이템 {itemId} 소비 알림 전송 완료 (총 {sentCount}명)");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[서버] 아이템 소비 알림 전송 오류 : {e.Message}");
        }
    }
    
    private void BroadcastPlayerEmoticon(int packetEmoticonId, int packetPlayerId)
    {
        try
        {
            NetworkPacket emoticonPacket = new NetworkPacket
            {
                Type = PacketType.PlayerEmoticon,
                PlayerId = packetPlayerId,
                EmoticonId = packetEmoticonId,
                Timestamp = DateTime.UtcNow
            };
            
            // byte[] data = emoticonPacket.ToBytes();
            int sentCount = 0;

            foreach (var existPlayer in _players.Values)
            {
                // _socket.SendTo(data, existPlayer.EndPoint);
                SendReliable(emoticonPacket, existPlayer.EndPoint);
                sentCount++;
            }
            
            _stats.IncrementSentPackets(sentCount);
            Console.WriteLine($"[서버] 플레이어 이모티콘 {packetEmoticonId} 사용 알림 전송 완료 (총 {sentCount}명)");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[서버] 이모티콘 알림 전송 오류 : {e.Message}");
        }
    }
    
    #endregion

    public void Dispose()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _players.Clear();
        _socket.Dispose();
        _stats.Dispose();
        _itemSpawnCts?.Cancel();
        _itemSpawnCts?.Dispose();
        _activeItems.Clear();
        
        Console.WriteLine("[서버] UDP 게임 서버 종료");
    }
}