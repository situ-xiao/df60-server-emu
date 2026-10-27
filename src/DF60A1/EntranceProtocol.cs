using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;

namespace DF60A1
{
    internal sealed class EntranceSession
    {
        public readonly byte[] Key = CreateKey();
        public readonly byte[] Token = CreateToken();
        private static byte[] CreateKey() { var key = new byte[32]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(key); return key; }
        private static byte[] CreateToken() { var token = new byte[16]; Encoding.ASCII.GetBytes("dnf2008-local").CopyTo(token, 0); return token; }
    }

    internal static class EntranceProtocol
    {
        public static bool IsEntrance(byte protocolId) { return protocolId == 11 || protocolId == 5 || protocolId == 9 || protocolId == 1; }

        public static EntrancePacket Handle(PacketFrame request, EntranceSession session, string host, int port, byte[] channelScript)
        {
            switch (request.ProtocolId)
            {
                case 11:
                    var keyPayload = new byte[36]; Buffer.BlockCopy(session.Key, 0, keyPayload, 4, session.Key.Length);
                    return new EntrancePacket(12, keyPayload);
                case 5:
                    var cache = new byte[32]; LittleEndian.WriteUInt32(cache, 0, channelScript != null && channelScript.Length > 0 ? 0u : 1u); Buffer.BlockCopy(session.Token, 0, cache, 4, session.Token.Length);
                    return new EntrancePacket(6, Encrypt(cache, session.Key));
                case 9:
                    return new EntrancePacket(10, Compress(Encrypt(channelScript ?? new byte[0], session.Key)));
                case 1:
                    return new EntrancePacket(3, Compress(Encrypt(BuildMetadata(host, port), session.Key)));
                default:
                    return null;
            }
        }

        private static byte[] BuildMetadata(string host, int port)
        {
            // sub_894B10 owns a fixed table of 109 server groups.  The client
            // keeps the currently selected group separately from the entrance
            // response, so sending only one sparse group can leave the visible
            // slot empty even though that group parsed successfully.  Populate
            // the complete table, as the original entrance service does.
            var groups = BuildServerGroups();
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(groups.Length);
                for (var index = 0; index < groups.Length; index++)
                {
                    WriteFixed(writer, groups[index], 20); writer.Write(1);
                    WriteFixed(writer, "Local Channel 1", 20); writer.Write(100); writer.Write(1);
                    WriteFixed(writer, host, 16); writer.Write(port);
                }
                return stream.ToArray();
            }
        }

        private static string[] BuildServerGroups()
        {
            var groups = new string[109];
            var koreanGroups = new[]
            {
                "cain", "diregie", "siroco", "prey",
                "casillas", "hilder", "ruke", "seria"
            };
            Array.Copy(koreanGroups, groups, koreanGroups.Length);
            for (var index = 0; index < 101; index++)
                groups[index + koreanGroups.Length] = "china_" + (index + 1);
            return groups;
        }

        private static void WriteFixed(BinaryWriter writer, string value, int width)
        {
            var bytes = Encoding.ASCII.GetBytes(value); if (bytes.Length >= width) throw new ArgumentException("字段过长: " + value);
            writer.Write(bytes); writer.Write(new byte[width - bytes.Length]);
        }

        private static byte[] Encrypt(byte[] plaintext, byte[] keyMaterial)
        {
            var paddedLength = Math.Max(16, (plaintext.Length + 15) / 16 * 16); var padded = new byte[paddedLength]; Buffer.BlockCopy(plaintext, 0, padded, 0, plaintext.Length);
            var key = new byte[16]; Buffer.BlockCopy(keyMaterial, 0, key, 0, 16);
            using (var aes = Aes.Create())
            { aes.KeySize = 128; aes.BlockSize = 128; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = key; using (var transform = aes.CreateEncryptor()) return transform.TransformFinalBlock(padded, 0, padded.Length); }
        }

        private static byte[] Compress(byte[] input)
        {
            using (var output = new MemoryStream())
            {
                var deflater = new Deflater(9, false);
                using (var zlib = new DeflaterOutputStream(output, deflater))
                { zlib.IsStreamOwner = false; zlib.Write(input, 0, input.Length); zlib.Finish(); }
                return output.ToArray();
            }
        }
    }
}
