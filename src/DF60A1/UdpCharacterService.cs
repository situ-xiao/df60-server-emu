using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace DF60A1
{
    internal sealed class UdpCharacterService : IDisposable
    {
        private readonly IPEndPoint _endpoint;
        private readonly Action<string, string, object[]> _log;
        private readonly ConcurrentDictionary<string, Pending> _sessions = new ConcurrentDictionary<string, Pending>();
        private UdpClient _udp;
        private volatile bool _running;

        public UdpCharacterService(IPEndPoint endpoint, Action<string, string, object[]> log) { _endpoint = endpoint; _log = log; }
        public void Start() { _running = true; _udp = new UdpClient(_endpoint); Task.Run((Func<Task>)LoopAsync); }
        public void Stop() { _running = false; if (_udp != null) _udp.Close(); }

        private async Task LoopAsync()
        {
            while (_running)
            {
                try
                {
                    var packet = await _udp.ReceiveAsync().ConfigureAwait(false);
                    await HandleAsync(packet.RemoteEndPoint, packet.Buffer).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (!_running) break; throw; }
            }
        }

        private async Task HandleAsync(IPEndPoint remote, byte[] datagram)
        {
            var key = remote.ToString(); Pending pending;
            if (_sessions.TryGetValue(key, out pending) && pending.WaitingBody)
            {
                pending.WaitingBody = false;
                await ReplyAsync(remote, pending.ProtocolId, datagram, pending).ConfigureAwait(false); return;
            }
            if (datagram.Length != 11) return;
            var total = (int)LittleEndian.ReadUInt32(datagram, 2); if (total < 11) return;
            pending = _sessions.GetOrAdd(key, x => new Pending()); pending.ProtocolId = datagram[1]; pending.WaitingBody = total > 11;
            if (!pending.WaitingBody) await ReplyAsync(remote, pending.ProtocolId, new byte[0], pending).ConfigureAwait(false);
        }

        private async Task ReplyAsync(IPEndPoint remote, byte protocol, byte[] body, Pending session)
        {
            byte replyId; byte[] payload;
            switch (protocol)
            {
                case 11: replyId = 12; payload = new byte[36]; Buffer.BlockCopy(session.Key, 0, payload, 4, 32); break;
                case 5: replyId = 6; payload = Encrypt(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, session.Key); break;
                case 1: replyId = 3; payload = Encrypt(new byte[16], session.Key); break;
                case 9: replyId = 10; payload = new byte[16]; break;
                default: return;
            }
            var header = new byte[11]; header[1] = replyId; LittleEndian.WriteUInt32(header, 2, (uint)(11 + payload.Length)); header[10] = 1;
            await _udp.SendAsync(header, header.Length, remote).ConfigureAwait(false);
            if (payload.Length > 0) await _udp.SendAsync(payload, payload.Length, remote).ConfigureAwait(false);
            _log("UDP", "{0} {1}->{2} payload={3}", new object[] { remote, protocol, replyId, payload.Length });
        }

        private static byte[] Encrypt(byte[] value, byte[] key)
        {
            using (var aes = Aes.Create())
            { aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = Slice(key, 16); using (var transform = aes.CreateEncryptor()) return transform.TransformFinalBlock(value, 0, value.Length); }
        }
        private static byte[] Slice(byte[] value, int count) { var result = new byte[count]; Buffer.BlockCopy(value, 0, result, 0, count); return result; }
        public void Dispose() { Stop(); }
        private sealed class Pending { public byte ProtocolId; public bool WaitingBody; public byte[] Key = new byte[32]; }
    }
}
