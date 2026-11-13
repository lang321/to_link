// REQ-REMOTE-20261105-03: no network or persistent configuration changes.
using System;
using System.Web.Script.Serialization;
namespace ToLink {
    public static class ServerSettingsTests {
        static void Check(bool ok, string label) { if (!ok) throw new Exception(label); }
        public static void Main() {
            Check(ServerSettings.Normalize("192.168.100.101:19000") == "ws://192.168.100.101:19000", "bare address");
            Check(ServerSettings.Normalize("https://EXAMPLE.com:443/") == "wss://example.com", "https address");
            foreach (var bad in new[] { "", "file:///etc/passwd", "http://user:pass@example.com", "http://example.com/path", "http://example.com?x=1", "http://example.com:0" }) {
                bool failed = false; try { ServerSettings.Normalize(bad); } catch (ArgumentException) { failed = true; } Check(failed, "invalid URL accepted");
            }
            var c = new AgentConfig { Server = "ws://host-a:19000", DeviceId = "111111111", Token = "token-a", AccessPassword = "password-a" };
            bool missingKey = false; try { ServerSettings.Switch(c, "host-b:19000", null); } catch (ArgumentException) { missingKey = true; }
            Check(missingKey && c.DeviceId == "111111111", "missing key mutated identity");
            ServerSettings.Switch(c, "host-b:19000", "registration-b");
            Check(c.DeviceId == null && c.Token == null && c.EnrollmentToken == "registration-b", "new server inherited credentials");
            c.DeviceId = "222222222"; c.Token = "token-b"; c.AccessPassword = "password-b"; c.EnrollmentToken = null;
            ServerSettings.Switch(c, "http://host-a:19000/", null);
            Check(c.DeviceId == "111111111" && c.Token == "token-a" && c.AccessPassword == "password-a", "old identity not restored");
            var json = new JavaScriptSerializer(); c = json.Deserialize<AgentConfig>(json.Serialize(c));
            ServerSettings.Switch(c, "host-b:19000", null);
            Check(c.DeviceId == "222222222" && c.AccessPassword == "password-b", "profiles not persisted");
            Console.WriteLine("Server settings normalization, validation, isolation, switching and persistence passed");
        }
    }
}
