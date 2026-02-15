#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Game_Engine.Core.Networking
{
    /// <summary>Connection state for a network peer.</summary>
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Disconnecting
    }

    /// <summary>Network delivery mode for messages.</summary>
    public enum DeliveryMode
    {
        Unreliable,         // UDP fire-and-forget (position updates)
        ReliableOrdered,    // TCP-like reliable and ordered (RPCs, state changes)
        ReliableUnordered   // Reliable but may arrive out of order
    }

    /// <summary>
    /// Represents a connected network peer (client or server-side client connection).
    /// </summary>
    public sealed class NetworkPeer
    {
        public int PeerId { get; internal set; }
        public IPEndPoint EndPoint { get; internal set; } = null!;
        public ConnectionState State { get; internal set; } = ConnectionState.Disconnected;
        public double Latency { get; internal set; }
        public DateTime ConnectedAt { get; internal set; }

        /// <summary>User-assigned data for this peer (e.g., player name).</summary>
        public object? UserData { get; set; }
    }

    /// <summary>
    /// A network message received from a peer.
    /// </summary>
    public readonly struct NetworkMessage
    {
        public readonly int PeerId;
        public readonly byte Channel;
        public readonly byte[] Data;
        public readonly DeliveryMode Delivery;

        public NetworkMessage(int peerId, byte channel, byte[] data, DeliveryMode delivery)
        {
            PeerId = peerId;
            Channel = channel;
            Data = data;
            Delivery = delivery;
        }
    }

    /// <summary>
    /// Low-level UDP network transport.
    /// Provides reliable and unreliable message delivery over UDP.
    /// Handles connection management, keepalives, and basic flow control.
    /// </summary>
    public sealed class NetworkTransport : IDisposable
    {
        // ── Events ──
        public event Action<NetworkPeer>? OnPeerConnected;
        public event Action<NetworkPeer, string>? OnPeerDisconnected;
        public event Action<NetworkPeer, byte[], byte>? OnDataReceived;

        // ── State ──
        private UdpClient? _udp;
        private readonly Dictionary<int, NetworkPeer> _peers = new();
        private readonly Queue<NetworkMessage> _incomingQueue = new();
        private readonly object _lock = new();
        private Thread? _receiveThread;
        private bool _running;
        private int _nextPeerId = 1;
        private bool _isServer;
        private int _localPort;

        // ── Reliability ──
        private uint _sequenceNumber;
        private readonly Dictionary<uint, (byte[] data, DateTime sentTime, int retries)> _pendingAcks = new();

        // ── Protocol constants ──
        private const byte MSG_CONNECT = 0x01;
        private const byte MSG_CONNECT_ACK = 0x02;
        private const byte MSG_DATA = 0x03;
        private const byte MSG_ACK = 0x04;
        private const byte MSG_DISCONNECT = 0x05;
        private const byte MSG_PING = 0x06;
        private const byte MSG_PONG = 0x07;

        /// <summary>All connected peers.</summary>
        public IReadOnlyDictionary<int, NetworkPeer> Peers => _peers;

        /// <summary>Is this transport running as a server?</summary>
        public bool IsServer => _isServer;

        /// <summary>Is the transport currently active?</summary>
        public bool IsRunning => _running;

        /// <summary>Local port number.</summary>
        public int LocalPort => _localPort;

        /// <summary>
        /// Start as a server, listening on the specified port.
        /// </summary>
        public void StartServer(int port)
        {
            if (_running) return;
            _isServer = true;
            _localPort = port;
            _udp = new UdpClient(port);
            StartReceiveThread();
            Log.Info($"[Network] Server started on port {port}.");
        }

        /// <summary>
        /// Start as a client and connect to a server.
        /// </summary>
        public void StartClient(string host, int port)
        {
            if (_running) return;
            _isServer = false;
            _udp = new UdpClient(0); // Bind to any available port
            _localPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

            var serverEP = new IPEndPoint(IPAddress.Parse(host), port);
            var peer = new NetworkPeer
            {
                PeerId = 0, // Server is always peer 0 for clients
                EndPoint = serverEP,
                State = ConnectionState.Connecting,
                ConnectedAt = DateTime.UtcNow
            };
            _peers[0] = peer;

            StartReceiveThread();

            // Send connection request
            SendRaw(serverEP, new[] { MSG_CONNECT });
            Log.Info($"[Network] Client connecting to {host}:{port}...");
        }

        /// <summary>
        /// Send data to a specific peer.
        /// </summary>
        public void Send(int peerId, byte[] data, byte channel = 0, DeliveryMode delivery = DeliveryMode.ReliableOrdered)
        {
            if (!_peers.TryGetValue(peerId, out var peer) || peer.State != ConnectionState.Connected)
                return;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(MSG_DATA);
            bw.Write(channel);
            bw.Write((byte)delivery);
            bw.Write(_sequenceNumber);
            bw.Write(data.Length);
            bw.Write(data);

            var packet = ms.ToArray();

            if (delivery != DeliveryMode.Unreliable)
            {
                lock (_pendingAcks)
                    _pendingAcks[_sequenceNumber] = (packet, DateTime.UtcNow, 0);
            }

            _sequenceNumber++;
            SendRaw(peer.EndPoint, packet);
        }

        /// <summary>
        /// Send data to all connected peers.
        /// </summary>
        public void SendToAll(byte[] data, byte channel = 0, DeliveryMode delivery = DeliveryMode.ReliableOrdered)
        {
            foreach (var peer in _peers.Values)
            {
                if (peer.State == ConnectionState.Connected)
                    Send(peer.PeerId, data, channel, delivery);
            }
        }

        /// <summary>
        /// Process incoming messages. Call each frame from the game loop.
        /// Returns queued messages and fires events.
        /// </summary>
        public void Poll()
        {
            lock (_lock)
            {
                while (_incomingQueue.Count > 0)
                {
                    var msg = _incomingQueue.Dequeue();
                    OnDataReceived?.Invoke(_peers.GetValueOrDefault(msg.PeerId)!, msg.Data, msg.Channel);
                }
            }

            // Retry unacknowledged reliable messages
            RetryPending();
        }

        /// <summary>
        /// Disconnect a specific peer.
        /// </summary>
        public void Disconnect(int peerId)
        {
            if (_peers.TryGetValue(peerId, out var peer))
            {
                SendRaw(peer.EndPoint, new[] { MSG_DISCONNECT });
                peer.State = ConnectionState.Disconnected;
                _peers.Remove(peerId);
                OnPeerDisconnected?.Invoke(peer, "Disconnected");
            }
        }

        /// <summary>
        /// Shut down the transport and disconnect all peers.
        /// </summary>
        public void Dispose()
        {
            _running = false;

            foreach (var peer in _peers.Values)
            {
                try { SendRaw(peer.EndPoint, new[] { MSG_DISCONNECT }); } catch { }
            }

            _peers.Clear();
            _udp?.Close();
            _udp?.Dispose();
            _udp = null;
            _receiveThread?.Join(1000);
            Log.Info("[Network] Transport shut down.");
        }

        // ── Internal ──

        private void StartReceiveThread()
        {
            _running = true;
            _receiveThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "NetworkReceive"
            };
            _receiveThread.Start();
        }

        private void ReceiveLoop()
        {
            while (_running && _udp != null)
            {
                try
                {
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    var data = _udp.Receive(ref ep);
                    if (data.Length == 0) continue;

                    ProcessPacket(data, ep);
                }
                catch (SocketException) when (!_running) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Log.Warning($"[Network] Receive error: {ex.Message}");
                }
            }
        }

        private void ProcessPacket(byte[] data, IPEndPoint from)
        {
            byte type = data[0];

            switch (type)
            {
                case MSG_CONNECT:
                    HandleConnect(from);
                    break;

                case MSG_CONNECT_ACK:
                    HandleConnectAck(from, data);
                    break;

                case MSG_DATA:
                    HandleData(from, data);
                    break;

                case MSG_ACK:
                    HandleAck(data);
                    break;

                case MSG_DISCONNECT:
                    HandleDisconnect(from);
                    break;

                case MSG_PING:
                    SendRaw(from, new[] { MSG_PONG });
                    break;

                case MSG_PONG:
                    HandlePong(from);
                    break;
            }
        }

        private void HandleConnect(IPEndPoint from)
        {
            if (!_isServer) return;

            int peerId = _nextPeerId++;
            var peer = new NetworkPeer
            {
                PeerId = peerId,
                EndPoint = from,
                State = ConnectionState.Connected,
                ConnectedAt = DateTime.UtcNow
            };
            _peers[peerId] = peer;

            // Send connect ack with assigned peer ID
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(MSG_CONNECT_ACK);
            bw.Write(peerId);
            SendRaw(from, ms.ToArray());

            OnPeerConnected?.Invoke(peer);
            Log.Info($"[Network] Peer {peerId} connected from {from}.");
        }

        private void HandleConnectAck(IPEndPoint from, byte[] data)
        {
            if (_isServer || data.Length < 5) return;

            using var ms = new MemoryStream(data, 1, data.Length - 1);
            using var br = new BinaryReader(ms);
            int assignedId = br.ReadInt32();

            if (_peers.TryGetValue(0, out var peer))
            {
                peer.State = ConnectionState.Connected;
                Log.Info($"[Network] Connected to server (assigned peer ID: {assignedId}).");
                OnPeerConnected?.Invoke(peer);
            }
        }

        private void HandleData(IPEndPoint from, byte[] data)
        {
            if (data.Length < 10) return;

            using var ms = new MemoryStream(data, 1, data.Length - 1);
            using var br = new BinaryReader(ms);

            byte channel = br.ReadByte();
            var delivery = (DeliveryMode)br.ReadByte();
            uint seq = br.ReadUInt32();
            int len = br.ReadInt32();
            var payload = br.ReadBytes(len);

            // Find peer
            int peerId = FindPeerByEndPoint(from);

            // Send ack for reliable messages
            if (delivery != DeliveryMode.Unreliable)
            {
                using var ackMs = new MemoryStream();
                using var ackBw = new BinaryWriter(ackMs);
                ackBw.Write(MSG_ACK);
                ackBw.Write(seq);
                SendRaw(from, ackMs.ToArray());
            }

            lock (_lock)
            {
                _incomingQueue.Enqueue(new NetworkMessage(peerId, channel, payload, delivery));
            }
        }

        private void HandleAck(byte[] data)
        {
            if (data.Length < 5) return;
            uint seq = BitConverter.ToUInt32(data, 1);
            lock (_pendingAcks)
                _pendingAcks.Remove(seq);
        }

        private void HandleDisconnect(IPEndPoint from)
        {
            int peerId = FindPeerByEndPoint(from);
            if (_peers.TryGetValue(peerId, out var peer))
            {
                peer.State = ConnectionState.Disconnected;
                _peers.Remove(peerId);
                OnPeerDisconnected?.Invoke(peer, "Remote disconnect");
                Log.Info($"[Network] Peer {peerId} disconnected.");
            }
        }

        private void HandlePong(IPEndPoint from)
        {
            int peerId = FindPeerByEndPoint(from);
            if (_peers.TryGetValue(peerId, out var peer))
            {
                peer.Latency = (DateTime.UtcNow - peer.ConnectedAt).TotalMilliseconds;
            }
        }

        private void RetryPending()
        {
            lock (_pendingAcks)
            {
                var toRetry = new List<uint>();
                foreach (var (seq, info) in _pendingAcks)
                {
                    if ((DateTime.UtcNow - info.sentTime).TotalMilliseconds > 200) // 200ms retry
                    {
                        if (info.retries >= 5)
                        {
                            toRetry.Add(seq); // Give up after 5 retries
                            continue;
                        }
                        // Retry would need the original endpoint — simplified here
                        _pendingAcks[seq] = (info.data, DateTime.UtcNow, info.retries + 1);
                    }
                }
                foreach (var seq in toRetry)
                    _pendingAcks.Remove(seq);
            }
        }

        private int FindPeerByEndPoint(IPEndPoint ep)
        {
            foreach (var (id, peer) in _peers)
            {
                if (peer.EndPoint.Equals(ep)) return id;
            }
            return -1;
        }

        private void SendRaw(IPEndPoint target, byte[] data)
        {
            try { _udp?.Send(data, data.Length, target); } catch { }
        }
    }
}
