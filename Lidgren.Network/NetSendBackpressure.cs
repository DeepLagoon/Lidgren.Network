using System;
using System.Collections.Generic;
using System.Net;

namespace Lidgren.Network;

internal enum NetSocketSendResult { Sent, WouldBlock, Failed }

// Only the network thread owns this queue. A short retry bridges local socket pressure;
// reliable delivery still uses the existing wire ACKs and retransmission windows.
internal sealed class NetPendingSendQueue(
    Func<byte[], int, IPEndPoint, NetSocketSendResult> send,
    Func<int, byte[]> rent,
    Action<byte[]> recycle,
    Action<int, int> sent)
{
    internal const int MaximumBytes = 64 * 1024;
    internal const int ControlReserveBytes = 16 * 1024;
    internal const double MaximumAge = 0.050;
    private readonly Queue<Packet> _control = new();
    private readonly Queue<Packet> _data = new();
    private record struct Packet(byte[] Data, int Length, IPEndPoint Target, int Messages, double Created);
    internal int Bytes { get; private set; }
    internal int Count => _control.Count + _data.Count;
    internal long WouldBlock { get; private set; }
    internal long Retried { get; private set; }
    internal long Expired { get; private set; }
    internal long Overflow { get; private set; }

    internal void Send(byte[] data, int length, IPEndPoint target, int messages, double now)
    {
        if (Count == 0)
        {
            var result = send(data, length, target);
            if (result == NetSocketSendResult.Sent) { sent(length, messages); return; }
            if (result != NetSocketSendResult.WouldBlock) return;
            WouldBlock++;
        }

        var control = length > 0 && data[0] >= (byte) NetMessageType.LibraryError;
        var limit = control ? MaximumBytes : MaximumBytes - ControlReserveBytes;
        if (length > limit - Bytes) { Overflow++; return; }
        var copy = rent(length);
        Buffer.BlockCopy(data, 0, copy, 0, length);
        (control ? _control : _data).Enqueue(new Packet(copy, length,
            new IPEndPoint(target.Address, target.Port), messages, now));
        Bytes += length;
    }

    internal void Drain(double now)
    {
        // Expire both classes even when the socket is still blocked. This bounds age,
        // not just memory, and keeps obsolete unreliable packets out of a long backlog.
        Expire(_control, now);
        Expire(_data, now);
        while (Count > 0)
        {
            var queue = _control.Count > 0 ? _control : _data;
            var packet = queue.Peek();
            var result = send(packet.Data, packet.Length, packet.Target);
            if (result == NetSocketSendResult.WouldBlock) { WouldBlock++; return; }
            if (result == NetSocketSendResult.Sent)
            {
                sent(packet.Length, packet.Messages);
                Retried++;
            }
            Release(queue.Dequeue());
        }
    }

    private void Expire(Queue<Packet> queue, double now)
    {
        while (queue.TryPeek(out var packet) && now - packet.Created >= MaximumAge)
        {
            Release(queue.Dequeue());
            Expired++;
        }
    }

    private void Release(Packet packet) { Bytes -= packet.Length; recycle(packet.Data); }

    internal void Clear()
    {
        while (_control.TryDequeue(out var packet)) Release(packet);
        while (_data.TryDequeue(out var packet)) Release(packet);
    }
}

public partial class NetPeer
{
    private NetPendingSendQueue? _pendingSends;
    private bool _sendWouldBlock;
    private bool _sendConnectionReset;
    private double _nextSendBufferWarning;
    private long _socketWouldBlockCount;

    internal bool CanSendQueuedMessages => !m_configuration.EnableSendBackpressure || _pendingSends?.Count is not > 0;
    public long SocketWouldBlockCount => _socketWouldBlockCount;
    public long SocketRetriedPackets => _pendingSends?.Retried ?? 0;
    public long SocketExpiredPackets => _pendingSends?.Expired ?? 0;
    public long SocketQueueOverflows => _pendingSends?.Overflow ?? 0;
    public int SocketPendingBytes => _pendingSends?.Bytes ?? 0;

    private void SendWithBackpressure(byte[] data, int length, IPEndPoint target, int messages, out bool reset)
    {
        reset = false;
        if (!m_configuration.EnableSendBackpressure)
        {
            if (ActuallySendPacket(data, length, target, out reset)) m_statistics.PacketSent(length, messages);
            return;
        }

        _pendingSends ??= new NetPendingSendQueue((bytes, count, endpoint) =>
        {
            if (ActuallySendPacket(bytes, count, endpoint, out _sendConnectionReset)) return NetSocketSendResult.Sent;
            return _sendWouldBlock ? NetSocketSendResult.WouldBlock : NetSocketSendResult.Failed;
        }, GetStorage, Recycle, m_statistics.PacketSent);
        _sendConnectionReset = false;
        _pendingSends.Send(data, length, target, messages, NetTime.Now);
        reset = _sendConnectionReset;
    }

    private void WarnSendBufferFull()
    {
        _sendWouldBlock = true;
        _socketWouldBlockCount++;
        if (NetTime.Now < _nextSendBufferWarning) return;
        _nextSendBufferWarning = NetTime.Now + 1;
        LogWarning("UDP socket would block; local send buffer full" +
                   (m_configuration.EnableSendBackpressure ? "; applying bounded send backpressure" : ""));
    }
}

// Shared across all reliable channels of one connection. Per-channel sequence
// windows alone allow a large burst when several channels are busy together.
internal sealed class NetReliableByteWindow
{
    internal int Bytes { get; private set; }
    internal int Limit { get; private set; }
    private int _maximum;
    private double _nextReduction;

    internal bool CanSend(int length, int maximum)
    {
        if (maximum <= 0) return true;
        if (_maximum != maximum)
        {
            _maximum = maximum;
            Limit = maximum;
        }
        return Bytes == 0 || length <= Limit - Bytes;
    }

    // Additive increase / multiplicative decrease: preserve a healthy connection's
    // initial throughput, then learn a smaller aggregate window from real pressure.
    // Several channels reporting the same episode must not each halve the window.
    internal void Congestion(double now, double interval)
    {
        if (Limit == 0 || now < _nextReduction) return;
        Limit = Math.Max(Math.Min(8192, _maximum), Limit / 2);
        _nextReduction = now + Math.Max(.025, interval);
    }

    internal void Acknowledge(int length)
    {
        Release(length);
        if (Limit > 0 && length > 0)
            Limit = (int)Math.Min(_maximum, Limit + Math.Max(1L, (long)length * 1024 / Limit));
    }
    internal void Reserve(int length) => Bytes = checked(Bytes + length);
    internal void Release(int length)
    {
        if (length < 0 || length > Bytes) throw new InvalidOperationException("Unbalanced reliable send byte window");
        Bytes -= length;
    }
}

public partial class NetConnection
{
    private int _nextReliableSendChannel = -1;
    internal readonly NetReliableByteWindow ReliableByteWindow = new();
    private long _lastSocketPressure;
    internal bool CanSendReliable(int length)
    {
        if (!m_peerConfiguration.EnableSendBackpressure) return true;
        var pressure = m_peer.SocketWouldBlockCount;
        if (pressure != _lastSocketPressure)
        {
            _lastSocketPressure = pressure;
            NoteReliableCongestion(NetTime.Now);
        }
        return ReliableByteWindow.CanSend(length, m_peerConfiguration.ReliableSendWindowBytes);
    }

    internal void NoteReliableCongestion(double now)
    {
        if (m_peerConfiguration.EnableSendBackpressure)
            ReliableByteWindow.Congestion(now, GetResendDelay());
    }
}
