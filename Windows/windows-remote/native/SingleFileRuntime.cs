// REQ-REMOTE-20261105-01: one distributed EXE; stream embedded dependencies into a per-user cache.
using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace ToLink {
    internal static class SingleFileRuntime {
        static Stream Resource(string name) {
            var stream = typeof(SingleFileRuntime).Assembly.GetManifestResourceStream(name);
            if (stream == null) throw new IOException("程序内置组件缺失：" + name);
            return stream;
        }
        static string Hash(string path) {
            using (var file = File.OpenRead(path)) using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }
        public static void Prepare(string directory) {
            Directory.CreateDirectory(directory);
            var encoder = Path.Combine(directory, "ffmpeg.exe");
            if (!File.Exists(encoder) || Hash(encoder) != SingleFileManifest.EncoderHash) {
                string temp = encoder + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try {
                    using (var input = Resource("ToLink.ffmpeg.gz"))
                    using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) gzip.CopyTo(output, 65536);
                    if (Hash(temp) != SingleFileManifest.EncoderHash) throw new IOException("内置编码器校验失败，请重新获取程序。");
                    if (File.Exists(encoder)) File.Replace(temp, encoder, null); else File.Move(temp, encoder);
                } finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            var config = Path.Combine(directory, "agent.json");
            if (!File.Exists(config)) {
                // Upgrade an adjacent portable installation only once. Never overwrite a registered identity.
                var adjacent = Path.Combine(Path.GetDirectoryName(typeof(SingleFileRuntime).Assembly.Location), "agent.json");
                if (File.Exists(adjacent) && !string.Equals(Path.GetFullPath(adjacent), Path.GetFullPath(config), StringComparison.OrdinalIgnoreCase)) File.Copy(adjacent, config, false);
                else using (var input = Resource("ToLink.agent.json")) using (var output = new FileStream(config, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
            }
            foreach (var name in new[] { "FFmpeg-LICENSE.txt", "THIRD-PARTY.txt" }) {
                using (var input = Resource("ToLink." + name)) using (var output = File.Create(Path.Combine(directory, name))) input.CopyTo(output);
            }
        }
    }
}
