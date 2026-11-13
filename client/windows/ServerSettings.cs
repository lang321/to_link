// REQ-REMOTE-20261105-03: keep device credentials scoped to a normalized server address.
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
namespace ToLink {
    public sealed class ServerProfile {
        public string DeviceId { get; set; }
        public string Token { get; set; }
        public string AccessPassword { get; set; }
        public string EnrollmentToken { get; set; }
    }
    public static class ServerSettings {
        public static string Normalize(string address) {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("请输入服务器地址");
            string value = address.Trim(); if (!value.Contains("://")) value = "http://" + value;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || string.IsNullOrEmpty(uri.Host) || uri.HostNameType == UriHostNameType.Unknown ||
                (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "ws" && uri.Scheme != "wss") ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" || uri.Port <= 0)
                throw new ArgumentException("地址格式应为 IP:端口 或 http(s)://域名，不包含路径、账号或参数");
            return new UriBuilder(uri.Scheme == "https" || uri.Scheme == "wss" ? "wss" : "ws", uri.Host, uri.Port).Uri.AbsoluteUri.TrimEnd('/');
        }
        public static void Remember(AgentConfig config) {
            if (config.Servers == null) config.Servers = new Dictionary<string, ServerProfile>();
            config.Servers[Normalize(config.Server)] = new ServerProfile { DeviceId = config.DeviceId, Token = config.Token, AccessPassword = config.AccessPassword, EnrollmentToken = config.EnrollmentToken };
        }
        public static bool NeedsEnrollment(AgentConfig config, string address) {
            string normalized = Normalize(address);
            if (Normalize(config.Server) == normalized) return string.IsNullOrEmpty(config.Token) && string.IsNullOrEmpty(config.EnrollmentToken);
            ServerProfile profile;
            return config.Servers == null || !config.Servers.TryGetValue(normalized, out profile) || (string.IsNullOrEmpty(profile.Token) && string.IsNullOrEmpty(profile.EnrollmentToken));
        }
        public static void Switch(AgentConfig config, string address, string registrationKey) {
            string normalized = Normalize(address);
            if (NeedsEnrollment(config, normalized) && string.IsNullOrEmpty(registrationKey)) throw new ArgumentException("首次连接该服务器需要注册密钥");
            Remember(config);
            ServerProfile profile;
            if (!config.Servers.TryGetValue(normalized, out profile)) {
                var bytes = new byte[12]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                profile = new ServerProfile { AccessPassword = Convert.ToBase64String(bytes), EnrollmentToken = registrationKey };
            }
            config.Server = normalized; config.DeviceId = profile.DeviceId; config.Token = profile.Token; config.AccessPassword = profile.AccessPassword;
            config.EnrollmentToken = string.IsNullOrEmpty(profile.EnrollmentToken) && string.IsNullOrEmpty(profile.Token) ? registrationKey : profile.EnrollmentToken;
            Remember(config);
        }
    }
}
