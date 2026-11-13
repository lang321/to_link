// REQ-REMOTE-20261105-01: identical real-screen fixture for before/after capture measurements.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
namespace ToLink {
    public static class PerformanceHarness {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [STAThread] public static void Main(string[] args) {
            SetProcessDPIAware(); Application.EnableVisualStyles();
            string mode = args[0], output = args[1];
            var form = new Form { Text = "ToLink 性能基准", WindowState = FormWindowState.Maximized, BackColor = Color.White, TopMost = true };
            form.Controls.Add(new Label { Text = "REQ-REMOTE-20261105-01  清晰度与性能基准 ABC 0123456789", Left = 60, Top = 60, Width = 1100, Height = 50, Font = new Font("Microsoft YaHei UI", 16) });
            var block = new Panel { Left = 60, Top = 200, Width = 120, Height = 120, BackColor = Color.Teal }; form.Controls.Add(block);
            int step = 0; var timer = new System.Windows.Forms.Timer { Interval = 50 }; timer.Tick += (s, e) => { step = (step + 9) % 1000; block.Left = 60 + step; };
            form.Shown += async (s, e) => {
                timer.Enabled = mode == "motion";
                try { await Task.Run(async () => {
                    using (var stop = new CancellationTokenSource()) {
                        var encoder = new VideoEncoder(); long bytes = 0, messages = 0;
                        var run = encoder.Run(new AgentConfig(), "h265", () => !stop.IsCancellationRequested,
                            value => Task.FromResult(0), data => { Interlocked.Add(ref bytes, data.Length); Interlocked.Increment(ref messages); return Task.FromResult(0); }, stop.Token);
                        await Task.Delay(3000);
                        var own = Process.GetCurrentProcess();
                        var child = (Process)typeof(VideoEncoder).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(encoder);
                        if (child == null || child.HasExited) throw new Exception("Encoder did not start");
                        own.Refresh(); child.Refresh(); double startCpu = own.TotalProcessorTime.TotalMilliseconds, childCpu = child.TotalProcessorTime.TotalMilliseconds;
                        long firstBytes = Interlocked.Read(ref bytes), firstMessages = Interlocked.Read(ref messages), peak = 0;
                        int gc = GC.CollectionCount(2); var elapsed = Stopwatch.StartNew();
                        for (int i = 0; i < 10; i++) { await Task.Delay(1000); own.Refresh(); child.Refresh(); peak = Math.Max(peak, own.WorkingSet64 + child.WorkingSet64); }
                        var result = new { mode = mode, seconds = elapsed.Elapsed.TotalSeconds, captureCpuPercentOneCore = (own.TotalProcessorTime.TotalMilliseconds - startCpu) / elapsed.Elapsed.TotalMilliseconds * 100,
                            encoderCpuPercentOneCore = (child.TotalProcessorTime.TotalMilliseconds - childCpu) / elapsed.Elapsed.TotalMilliseconds * 100, combinedWorkingSetMiB = peak / 1048576.0,
                            bytes = Interlocked.Read(ref bytes) - firstBytes, fragments = Interlocked.Read(ref messages) - firstMessages, gen2Collections = GC.CollectionCount(2) - gc };
                        stop.Cancel(); encoder.Stop(); try { await run; } catch (OperationCanceledException) { }
                        File.WriteAllText(output, new JavaScriptSerializer().Serialize(result));
                    }
                }); } catch (Exception ex) { File.WriteAllText(output, new JavaScriptSerializer().Serialize(new { error = ex.ToString() })); }
                timer.Stop(); form.Close();
            };
            Application.Run(form); timer.Dispose();
        }
    }
}
