using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace DF60A1
{
    internal sealed class PacketFrame
    {
        // The entrance service keeps the legacy ten-byte frame. Arad 60
        // channel commands use a thirteen-byte frame instead (u16 id and a
        // separate u16 sequence after the checksum).
        public const int HeaderLength = 10;
        public const int CitrisCommandHeaderLength = 13;
        public byte Type;
        public byte ProtocolId;
        public uint DeclaredCrc32;
        public byte[] Body;
        public bool IsCitrisCommand;

        public bool HasValidCrc32
        {
            get
            {
                return DeclaredCrc32 == 0 || DeclaredCrc32 ==
                    (IsCitrisCommand ? Citris60Crc32.Compute(Body) : Crc32.Compute(Body));
            }
        }

        public static async Task<PacketFrame> ReadAsync(Stream stream)
        {
            var prefix = new byte[7];
            if (!await ReadExactAsync(stream, prefix, true).ConfigureAwait(false)) return null;

            if (prefix[0] == 1 && !GameServerPacket.UseLegacyChannelHeader)
            {
                var citrisProtocolId = LittleEndian.ReadUInt16(prefix, 1);
                var citrisLength = LittleEndian.ReadUInt32(prefix, 3);
                var legacyLengthGuess = LittleEndian.ReadUInt32(prefix, 2);
                var citrisPlausible = citrisProtocolId <= byte.MaxValue &&
                    citrisLength >= CitrisCommandHeaderLength && citrisLength <= 4 * 1024 * 1024;
                var legacyPlausible = legacyLengthGuess >= HeaderLength && legacyLengthGuess <= 4 * 1024 * 1024;

                if (citrisPlausible && !legacyPlausible)
                {
                    var protocolId = (byte)citrisProtocolId;
                    var length = citrisLength;
                    var suffix = new byte[CitrisCommandHeaderLength - prefix.Length];
                    if (!await ReadExactAsync(stream, suffix, false).ConfigureAwait(false)) throw new EndOfStreamException();
                    var encryptedPayload = new byte[(int)length - CitrisCommandHeaderLength];
                    if (encryptedPayload.Length > 0 && !await ReadExactAsync(stream, encryptedPayload, false).ConfigureAwait(false))
                        throw new EndOfStreamException();

                    // Preserve the old server's internal body convention:
                    // sequence first, followed by the business payload. This
                    // keeps the existing command parsers (whose offsets start at
                    // two) reusable while the Arad transport is adapted.
                    var body = new byte[2 + encryptedPayload.Length];
                    body[0] = suffix[4]; body[1] = suffix[5];
                    Buffer.BlockCopy(encryptedPayload, 0, body, 2, encryptedPayload.Length);
                    return new PacketFrame
                    {
                        Type = prefix[0],
                        ProtocolId = protocolId,
                        DeclaredCrc32 = LittleEndian.ReadUInt32(suffix, 0),
                        Body = body,
                        IsCitrisCommand = true
                    };
                }
            }

            var header = new byte[HeaderLength];
            Buffer.BlockCopy(prefix, 0, header, 0, prefix.Length);
            var remainder = new byte[HeaderLength - prefix.Length];
            if (!await ReadExactAsync(stream, remainder, false).ConfigureAwait(false)) throw new EndOfStreamException();
            Buffer.BlockCopy(remainder, 0, header, prefix.Length, remainder.Length);
            var legacyLength = LittleEndian.ReadUInt32(header, 2);
            if (legacyLength < HeaderLength || legacyLength > 4 * 1024 * 1024) throw new InvalidDataException("无效 DNF 包长度: " + legacyLength);
            var legacyBody = new byte[(int)legacyLength - HeaderLength];
            if (legacyBody.Length > 0 && !await ReadExactAsync(stream, legacyBody, false).ConfigureAwait(false)) throw new EndOfStreamException();
            return new PacketFrame { Type = header[0], ProtocolId = header[1], DeclaredCrc32 = LittleEndian.ReadUInt32(header, 6), Body = legacyBody };
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, bool eofAllowed)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer, offset, buffer.Length - offset).ConfigureAwait(false);
                if (count == 0) return eofAllowed && offset == 0 ? false : false;
                offset += count;
            }
            return true;
        }
    }

    internal sealed class GameServerPacket
    {
        public const int HeaderLength = 7;
        public const int LegacyHeaderLength = 6;
        // Arad 老客户端使用 6 字节下行包头（u8 type + u8 protocol + u32 length），
        // Arad 60 使用 7 字节包头（u8 type + u16 protocol + u32 length）。
        // 默认保持 Arad 布局；启动服务端时指定 --legacy-channel-header 切换。
        public static bool UseLegacyChannelHeader = true;
        // 新手教程开关：开启时新角色（TutorialStarted=false）的选角模块掩码为 0，
        // 通知 136 会启动教程（事件/漫画/地图）；关闭时掩码置满直接进城镇。
        public static bool TutorialEnabled = true;
        public readonly byte Type;
        public readonly byte ProtocolId;
        public readonly byte[] Payload;

        public GameServerPacket(byte type, byte protocolId, byte[] payload)
        {
            Type = type; ProtocolId = protocolId; Payload = payload ?? new byte[0];
        }

        public byte[] Encode()
        {
            // Arad 60 (DNF.exe product 1.0.44.1) reads server packets as
            // u8 type + u16 protocolId + u32 totalLength.  The Arad client
            // used a one-byte protocol id and therefore only a six-byte
            // header; sending that layout makes this client read a corrupt
            // length and immediately close the socket.
            if (UseLegacyChannelHeader)
            {
                var legacy = new byte[LegacyHeaderLength + Payload.Length];
                legacy[0] = Type;
                legacy[1] = ProtocolId;
                LittleEndian.WriteUInt32(legacy, 2, (uint)legacy.Length);
                Buffer.BlockCopy(Payload, 0, legacy, LegacyHeaderLength, Payload.Length);
                GamePayloadCipher.Encrypt(legacy, LegacyHeaderLength, Payload.Length);
                return legacy;
            }
            var output = new byte[HeaderLength + Payload.Length];
            output[0] = Type;
            LittleEndian.WriteUInt16(output, 1, ProtocolId);
            LittleEndian.WriteUInt32(output, 3, (uint)output.Length);
            Buffer.BlockCopy(Payload, 0, output, HeaderLength, Payload.Length);
            GamePayloadCipher.Encrypt(output, HeaderLength, Payload.Length);
            return output;
        }

        public async Task WriteAsync(Stream stream)
        {
            var packet = Encode();
            await stream.WriteAsync(packet, 0, packet.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
    }

    internal sealed class EntrancePacket
    {
        public readonly byte ProtocolId;
        public readonly byte[] Payload;
        public EntrancePacket(byte protocolId, byte[] payload) { ProtocolId = protocolId; Payload = payload ?? new byte[0]; }
        public byte[] Encode()
        {
            var output = new byte[PacketFrame.HeaderLength + 1 + Payload.Length];
            output[0] = 0; output[1] = ProtocolId; LittleEndian.WriteUInt32(output, 2, (uint)output.Length);
            output[10] = 1; Buffer.BlockCopy(Payload, 0, output, 11, Payload.Length); return output;
        }
        public async Task WriteAsync(Stream stream)
        {
            var output = Encode(); await stream.WriteAsync(output, 0, output.Length).ConfigureAwait(false); await stream.FlushAsync().ConfigureAwait(false);
        }
    }

    internal static class GamePayloadCipher
    {
        // DNF.exe 1.0.1.9, sub_40D6D0, copies the six-byte-header packet's
        // wire payload and decodes it through sub_776F80(payload, size, 0):
        //     plain = ROL6(cipher) XOR 0xB5
        // Server encoding must therefore apply the inverse operation below.
        private const byte XorKey = 0xB5;
        private const int Rotation = 6;

        public static void Encrypt(byte[] bytes, int offset, int count)
        {
            for (var index = offset; index < offset + count; index++)
                bytes[index] = RotateRight((byte)(bytes[index] ^ XorKey), Rotation);
        }

        public static void Decrypt(byte[] bytes, int offset, int count)
        {
            for (var index = offset; index < offset + count; index++)
                bytes[index] = (byte)(RotateLeft(bytes[index], Rotation) ^ XorKey);
        }

        private static byte RotateLeft(byte value, int count) { return (byte)((value << count) | (value >> (8 - count))); }
        private static byte RotateRight(byte value, int count) { return (byte)((value >> count) | (value << (8 - count))); }
    }

    internal sealed class GameClientCipherState
    {
        private List<uint> _seeds = CreateSeeds();
        public int CandidateCount { get { return _seeds.Count; } }

        public bool TryDecode(PacketFrame frame, out PacketFrame decoded)
        {
            decoded = frame;
            if (frame.Body.Length < 2 || _seeds.Count == 0) return false;
            if (frame.Body.Length == 2)
            {
                AdvanceAll();
                return frame.HasValidCrc32;
            }
            if (frame.DeclaredCrc32 == 0) return false;

            var nextSeeds = new List<uint>();
            byte[] chosen = null;
            var signatures = new Dictionary<int, byte[]>();
            var failures = new HashSet<int>();
            foreach (var originalSeed in _seeds)
            {
                var seed = originalSeed;
                var random = GameClientPayloadCipher.Advance(ref seed);
                var key = (byte)random;
                var rotation = (int)((random >> 8) & 7);
                var signature = key | (rotation << 8);
                byte[] plain;
                if (failures.Contains(signature)) continue;
                if (!signatures.TryGetValue(signature, out plain))
                {
                    plain = (byte[])frame.Body.Clone();
                    GameClientPayloadCipher.Decrypt(frame.Body, 2, plain, 2, frame.Body.Length - 2, key, rotation);
                    if (Crc32.Compute(plain) != frame.DeclaredCrc32)
                    {
                        failures.Add(signature);
                        continue;
                    }
                    signatures[signature] = plain;
                }
                nextSeeds.Add(seed);
                if (chosen == null) chosen = plain;
            }
            if (chosen == null)
            {
                // DNF.exe 1.0.1.9 can choose a fresh 15-bit transform seed for
                // each command instead of continuing from the previous packet.
                // Keep rolling-seed compatibility, but retry this packet from
                // the complete seed space when the carried state cannot match.
                if (_seeds.Count != 0x8000)
                {
                    _seeds = CreateSeeds();
                    return TryDecode(frame, out decoded);
                }
                return false;
            }
            _seeds = nextSeeds;
            decoded = new PacketFrame { Type = frame.Type, ProtocolId = frame.ProtocolId, DeclaredCrc32 = frame.DeclaredCrc32, Body = chosen };
            return true;
        }

        private void AdvanceAll()
        {
            for (var index = 0; index < _seeds.Count; index++) { var seed = _seeds[index]; GameClientPayloadCipher.Advance(ref seed); _seeds[index] = seed; }
        }

        private static List<uint> CreateSeeds()
        {
            var result = new List<uint>(0x8000);
            for (uint seed = 0; seed < 0x8000; seed++) result.Add(seed);
            return result;
        }
    }

    internal sealed class Citris60ClientCipher
    {
        // Notification 1 selects key slot zero. sub_8DCBC0 builds that slot
        // from the sixteen embedded key rows; the resulting AES-128 key is
        // shown below. Commands encrypt only the bytes after the two-byte
        // sequence number, in independent ECB blocks with zero padding.
        private static readonly byte[] Key =
        {
            0x37, 0x3A, 0x3B, 0x3A, 0x37, 0x38, 0x36, 0x37,
            0x35, 0x3B, 0x35, 0x31, 0x3B, 0x33, 0x38, 0x35
        };

        public bool TryDecode(PacketFrame frame, out PacketFrame decoded)
        {
            decoded = frame;
            if (frame.Body == null || frame.Body.Length < 2) return false;

            var plain = (byte[])frame.Body.Clone();
            var encryptedLength = plain.Length - 2;
            if (encryptedLength != 0)
            {
                if ((encryptedLength & 15) != 0) return false;
                using (var aes = Aes.Create())
                {
                    aes.Mode = CipherMode.ECB;
                    aes.Padding = PaddingMode.None;
                    aes.Key = Key;
                    using (var transform = aes.CreateDecryptor())
                        transform.TransformBlock(frame.Body, 2, encryptedLength, plain, 2);
                }
            }

            if (Citris60Crc32.Compute(plain) != frame.DeclaredCrc32) return false;
            decoded = new PacketFrame
            {
                Type = frame.Type,
                ProtocolId = frame.ProtocolId,
                DeclaredCrc32 = frame.DeclaredCrc32,
                Body = plain,
                IsCitrisCommand = frame.IsCitrisCommand
            };
            return true;
        }
    }

    internal static class Citris60Crc32
    {
        // sub_AA6D20 constructs this reflected table at runtime. It is CRC-32
        // framing with the Arad polynomial rather than the common IEEE one.
        private const uint Polynomial = 0x4DB89129u;
        private static readonly uint[] Table = CreateTable();

        public static uint Compute(byte[] bytes)
        {
            var crc = uint.MaxValue;
            for (var index = 0; index < bytes.Length; index++)
                crc = Table[(crc ^ bytes[index]) & 0xff] ^ (crc >> 8);
            return ~crc;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? Polynomial ^ (value >> 1) : value >> 1;
                table[index] = value;
            }
            return table;
        }
    }

    internal static class GameClientPayloadCipher
    {
        public static uint Advance(ref uint seed)
        {
            var first = Next(seed); var second = Next(first); var third = Next(second); seed = third;
            var high11 = (first >> 16) & 0x7ff; var high10 = (second >> 16) & 0x3ff; var final10 = (third >> 16) & 0x3ff;
            return final10 ^ ((high10 ^ (high11 << 10)) << 10);
        }

        public static void Decrypt(byte[] source, int sourceOffset, byte[] target, int targetOffset, int count, byte key, int rotation)
        {
            for (var index = 0; index < count; index++)
            {
                var value = (byte)(source[sourceOffset + index] ^ key);
                target[targetOffset + index] = rotation == 0 ? value : (byte)((value >> rotation) | (value << (8 - rotation)));
            }
        }

        private static uint Next(uint value) { return unchecked(value * 0x41C64E6D + 0x3039); }
    }

    internal static class Crc32
    {
        private static readonly uint[] Table = CreateTable();
        public static uint Compute(byte[] bytes)
        {
            return Compute(bytes, 0);
        }
        public static uint Compute(byte[] bytes, uint seed)
        {
            var crc = ~seed;
            for (var index = 0; index < bytes.Length; index++) crc = Table[(crc ^ bytes[index]) & 0xff] ^ (crc >> 8);
            return ~crc;
        }
        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < 256; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[index] = value;
            }
            return table;
        }
    }

    internal static class LittleEndian
    {
        public static ushort ReadUInt16(byte[] value, int offset) { return (ushort)(value[offset] | value[offset + 1] << 8); }
        public static short ReadInt16(byte[] value, int offset) { return unchecked((short)ReadUInt16(value, offset)); }
        public static uint ReadUInt32(byte[] value, int offset) { return (uint)(value[offset] | value[offset + 1] << 8 | value[offset + 2] << 16 | value[offset + 3] << 24); }
        public static void WriteUInt16(byte[] value, int offset, ushort number) { value[offset] = (byte)number; value[offset + 1] = (byte)(number >> 8); }
        public static void WriteUInt32(byte[] value, int offset, uint number) { value[offset] = (byte)number; value[offset + 1] = (byte)(number >> 8); value[offset + 2] = (byte)(number >> 16); value[offset + 3] = (byte)(number >> 24); }
    }
}
