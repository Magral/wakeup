long now = 100;
var ledger = new CommandLedger(() => now);
ledger.Remember("nonce", "SHUTDOWN", "127.0.0.1:3000", "accepted");
if (ledger.Find("nonce", "SHUTDOWN", "127.0.0.1:3000") != "accepted") throw new Exception("Lost acknowledgement cannot be recovered");
if (ledger.Find("nonce", "LAUNCH", "127.0.0.1:3000") != null) throw new Exception("Different command accepted");
if (ledger.Find("nonce", "SHUTDOWN", "127.0.0.1:4000") != null) throw new Exception("Different source accepted");
now += 30001;
if (ledger.Find("nonce", "SHUTDOWN", "127.0.0.1:3000") != null) throw new Exception("Expired result retained");
Console.WriteLine("Command ledger binding and expiration checks passed.");
