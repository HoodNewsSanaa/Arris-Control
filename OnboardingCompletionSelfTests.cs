using System.IO;

namespace ArrisControl;

internal static class OnboardingCompletionSelfTests
{
    public static async Task RunAsync()
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static async Task Refuse(Func<Task> work)
        {
            try { await work(); } catch (RouterException) { return; } catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("Invalid local completion was accepted.");
        }
        var before = new LocalOnboardingState(new("fixture", "TESTSERIAL123", "001122334455", "192.168.7.1"),
            Convert.ToBase64String(new byte[32]), "W30", "WR11AXER1", "AXR.0814.240522.10", "BRIDGE", "1", "1", "0", "0", DateTimeOffset.UtcNow);
        Check(CertificatePin.ForReconnect(null,before.Pin)==before.Pin && CertificatePin.ForReconnect(before.Pin,before.Pin)==before.Pin,
            "A fresh login dropped the certificate key already verified in this session.");
        await Refuse(()=>{CertificatePin.ForReconnect(Convert.ToBase64String(Enumerable.Repeat((byte)1,32).ToArray()),before.Pin);return Task.CompletedTask;});
        Settings.ValidateEnvelope(LocalCompletionPlan.Body);
        foreach (var body in new[]
        {
            """{"REQUEST":{"OPERTYPE":"MODIFY","TURNOFFBLE":"0"}}""",
            """{"REQUEST":{"OPERTYPE":"MODIFY","TURNOFFBLE":true}}""",
            """{"REQUEST":{"OPERTYPE":"MODIFY","TURNOFFBLE":{"ENABLE":"1"}}}""",
            """{"REQUEST":{"OPERTYPE":"MODIFY","TURNOFFBLE":"1","IPADDRESS":"192.168.7.2"}}""",
            """{"REQUEST":{"OPERTYPE":"MODIFY","TURNOFFOBA":{"OFFWIFI":"1"}}}"""
        }) await Refuse(() => { Settings.ValidateEnvelope(body); return Task.CompletedTask; });
        Check(!WobaProtocol.Allows("/setData", LocalCompletionPlan.Body), "Local completion entered the WOBA transport.");
        foreach (var scenario in new[] { "stopped", "unknown-service", "unknown-flag", "complete", "busy", "booting", "W31", "firmware", "hardware", "pin", "address", "cancel", "changed-mode", "changed-serial", "changed-mac", "changed-key" })
        {
            var fresh = scenario switch
            {
                "stopped" => before with { BluetoothService = "0" },
                "unknown-service" => before with { BluetoothService = "" },
                "unknown-flag" => before with { Onboarding = "" },
                "complete" => before with { Onboarding = "0" },
                "busy" => before with { Updating = "1" },
                "booting" => before with { Booting = "1" },
                "W31" => before with { Model = "W31" },
                "firmware" => before with { Firmware = "other" },
                "hardware" => before with { Hardware = "other" },
                "pin" => before with { Pin = "" },
                "address" => before with { Router = before.Router with { Address = "8.8.8.8" } },
                "changed-mode" => before with { Mode = "DUAL" },
                "changed-serial" => before with { Router = before.Router with { Serial = "OTHERROUTER1" } },
                "changed-mac" => before with { Router = before.Router with { Mac = "001122334499" } },
                "changed-key" => before with { Pin = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()) },
                _ => before
            };
            var writes = 0;
            var plan = LocalCompletionPlan.Prepare(before);
            using var stop = new CancellationTokenSource(); if (scenario == "cancel") stop.Cancel();
            await Refuse(() => plan.ApplyAsync(_ => Task.FromResult(fresh), (_, _) => { writes++; return Task.CompletedTask; }, stop.Token));
            await Refuse(() => plan.ApplyAsync(_ => Task.FromResult(before), (_, _) => { writes++; return Task.CompletedTask; }, CancellationToken.None));
            Check(writes == 0, "A guard failure or second attempt sent completion: " + scenario);
        }
        foreach (var outcome in new[] { "complete", "unchanged", "unknown", "lost", "read-failed", "wrong-after", "mode-after" })
        {
            var reads = 0; var writes = 0;
            var plan = LocalCompletionPlan.Prepare(before);
            Task<LocalOnboardingState> Read(CancellationToken token)
            {
                reads++;
                if (reads == 1) return Task.FromResult(before);
                if (outcome == "read-failed") throw new IOException("Fictional disconnected interface.");
                return Task.FromResult(outcome switch
                {
                    "complete" => before with { Onboarding = "0", BluetoothService = "0" },
                    "unknown" => before with { Onboarding = "" },
                    "wrong-after" => before with { Router = before.Router with { Serial = "OTHERROUTER1" } },
                    "mode-after" => before with { Onboarding = "0", Mode = "DUAL" },
                    _ => before with { BluetoothService = "0" }
                });
            }
            var result = await plan.ApplyAsync(Read, (body, _) =>
            {
                Check(body == LocalCompletionPlan.Body, "Completion payload changed."); writes++;
                if (outcome == "lost") throw new IOException("Fictional lost acknowledgement.");
                return Task.CompletedTask;
            }, CancellationToken.None);
            Check(writes == 1 && result.Complete == (outcome == "complete"), "Completion was repeated or inferred from an acknowledgement: " + outcome);
            Check(outcome != "lost" || !result.Acknowledged && reads == 1, "An uncertain write was replayed or treated as acknowledged.");
            await Refuse(() => plan.ApplyAsync(Read, (_, _) => { writes++; return Task.CompletedTask; }, CancellationToken.None));
            Check(writes == 1, "A closed completion plan was reused.");
        }
        Check(RouterStatus.BluetoothService("0", "fixture").Value == "Stopped" &&
            RouterStatus.BluetoothService("", "fixture").Level == RouterStatusLevel.Unknown,
            "Stopped/unknown onboarding process was shown as running.");
        Console.WriteLine("PASS: local onboarding completion exact request, service/identity/update guards, bridge-mode independence, fresh flag verification and no uncertain-write retry.");
    }
}
