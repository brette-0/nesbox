using System.Net;
using System.Net.Sockets;
using dap;

var             logPath = Path.Combine(Path.GetTempPath(), "nesbox-dap-adaptor.log");
await using var log     = new StreamWriter(logPath, append: false);
log.AutoFlush = true;

await log.WriteLineAsync($"[{DateTime.Now}] DAP Adaptor starting...");

var port     = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 4711;
var listener = new TcpListener(IPAddress.Loopback, port);

listener.Start();

await log.WriteLineAsync($"[{DateTime.Now}] DAP Adaptor listening on {port}");
Console.Error.WriteLine($"DAP adaptor listening on 172.0.0.1:{port}");  // seems cheap hack

while (true) {
    var client = await listener.AcceptTcpClientAsync();
    await log.WriteLineAsync($"[{DateTime.Now}] DAP adaptor connected");
    var session = new DapSession(client, log);
    _ = Task.Run(() => session.RunAsync());
}