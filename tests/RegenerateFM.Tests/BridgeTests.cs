using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using RegenerateFM.Desktop.FM24.Bridge;
using Xunit;

namespace RegenerateFM.Tests
{
    // The companion's requests to FM24's DLL, and the DLL's clicks to the companion.
    public class BridgeTests
    {
        private const string Token = "kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk";

        private static string Decode(string base64) => Encoding.UTF8.GetString(Convert.FromBase64String(base64));

        [Fact]
        public void AConfirmationWaitsForTheAnswerAndSendsItsTextAsBase64()
        {
            using var dll = new FakeDll("queued", "shown", "shown", "yes");
            Assert.Equal("yes", Dll.ShowDialog(dll.Port, Token, "confirm", "Generate faces?", "4 of 29 players have no face."));
            var first = dll.Received[0];
            Assert.Equal(("REGENERATEFM24D", "1", "confirm"), (first[0], first[1], first[4]));
            Assert.Equal("4 of 29 players have no face.", Decode(first[6]));
            // Polling repeats the same serial, so the DLL never opens a second dialog.
            Assert.Single(dll.Received.Select(fields => fields[2]).Distinct());
        }

        [Fact]
        public void TheMacKeyPromptReturnsTheTypedKeyOrNull()
        {
            using var dll = new FakeDll("shown", "shown", "key " + Convert.ToBase64String(Encoding.UTF8.GetBytes("sk-typed")));
            Assert.Equal("sk-typed", Dll.AskKey(dll.Port, Token, "Paste it.\n\nKept on this Mac."));
            Assert.Equal(("REGENERATEFM24K", "1"), (dll.Received[0][0], dll.Received[0][1]));
            // Line breaks are kept: the prompt shows paragraphs.
            Assert.Equal("Paste it.\n\nKept on this Mac.", Decode(dll.Received[0][4]));
            using var cancel = new FakeDll("shown", "cancel");
            Assert.Null(Dll.AskKey(cancel.Port, Token, "Paste it."));
            using var busy = new FakeDll("busy");
            Assert.Throws<DialogUnavailableException>(() => Dll.AskKey(busy.Port, Token, "Paste it."));
        }

        [Fact]
        public void AMessageReturnsOnceShownAndABusyDllMeansTheFallback()
        {
            using (var dll = new FakeDll("queued", "shown"))
                Assert.Equal("shown", Dll.ShowDialog(dll.Port, Token, "message", "RegenerateFM", "Done."));
            foreach (string status in new[] { "busy", "failed" })
            {
                using var dll = new FakeDll(status);
                Assert.Throws<DialogUnavailableException>(() => Dll.ShowDialog(dll.Port, Token, "confirm", "Title", "Text"));
            }
            var fallback = new List<string>();
            using (var dll = new FakeDll("busy"))
                Dll.Notify(dll.Port, Token, "Error text", fallback.Add);
            Dll.Notify(null, Token, "No DLL", fallback.Add);
            Assert.Equal(new[] { "Error text", "No DLL" }, fallback);
            Assert.Throws<ArgumentException>(() => Dll.ShowDialog(1, Token, "confirm", "", "Text"));
            Assert.Throws<ArgumentException>(() => Dll.ShowDialog(1, Token, "confirm", "Title", new string('x', 2000)));
            Assert.Throws<ArgumentException>(() => Dll.ShowDialog(1, Token, "shell", "Title", "Text"));
        }

        [Fact]
        public void ProgressTextIsSetAndCleared()
        {
            using var dll = new FakeDll("queued", "done");
            Dll.SetProgress(dll.Port, Token, "portrait", "Generating face…");
            var first = dll.Received[0];
            Assert.Equal(("REGENERATEFM24P", "1", "portrait"), (first[0], first[1], first[4]));
            Assert.Equal("Generating face…", Decode(first[5]));
            Dll.SetProgress(dll.Port, Token, "portrait", "");
            Assert.Equal("-", dll.Received.Last()[5]);
            using var failed = new FakeDll("failed");
            Assert.Throws<DialogUnavailableException>(() => Dll.SetProgress(failed.Port, Token, "dialog", "2 of 4"));
        }

        [Fact]
        public void TheSquadProgressDialogReportsStopAndBecomesTheResult()
        {
            using (var dll = new FakeDll("queued", "shown", "shown", "yes"))
            {
                using var progress = new Dll.Progress(dll.Port, Token, "RegenerateFM squad faces", "Generating A (1/2)");
                Assert.True(progress.Stopped.Wait(5000));
                Assert.Equal("progress", dll.Received[0][4]);
                // Answered (Stop), so the result needs a new message, not the old dialog.
                dll.Script("shown");
                var fallback = new List<string>();
                progress.Finish("Generated 1 squad face.", fallback.Add);
                Assert.Equal("message", dll.Received.Last()[4]);
                Assert.Empty(fallback);
            }
            using (var dll = new FakeDll("queued", "shown"))
            {
                var progress = new Dll.Progress(dll.Port, Token, "RegenerateFM squad faces", "Generating A (1/1)");
                dll.Script("done");
                progress.Finish("Generated 1 squad face.", _ => Assert.Fail("fallback used"));
                Assert.Equal(("REGENERATEFM24P", "finish"), (dll.Received.Last()[0], dll.Received.Last()[4]));
            }
            // An update after Stop doesn't open the dialog again.
            var answered = new ManualResetEventSlim();
            using (var dll = new FakeDll())
            {
                dll.Answer = fields => fields[0] == "REGENERATEFM24P" ? "failed" : answered.IsSet ? "yes" : "shown";
                using var progress = new Dll.Progress(dll.Port, Token, "RegenerateFM squad faces", "Generating A (1/2)");
                answered.Set();
                progress.Update("Loaded A (1/2)");
                Assert.True(progress.Stopped.IsSet);
                Assert.Single(dll.Received.Where(fields => fields[0] == "REGENERATEFM24D").Select(fields => fields[2]).Distinct());
            }
        }

        [Fact]
        public void TheReloadPollsTheSameSerialUntilDone()
        {
            using (var dll = new FakeDll())
            {
                var statuses = new Queue<string>(new[] { "queued", "wrong-serial", "queued", "done" });
                var answers = new ConcurrentQueue<string>();
                dll.Answer = fields =>
                {
                    string status = statuses.Dequeue();
                    return status == "wrong-serial" ? null : status;
                };
                Dll.RequestReload(dll.Port, new string('t', 43), serial: 7);
                Assert.Equal(new[] { "REGENERATEFM24R 1 7 " + new string('t', 43) }, dll.Received.Select(fields => string.Join(" ", fields)).Distinct());
                Assert.Equal(4, dll.Received.Count);
            }
            foreach (string status in new[] { "failed", "busy" })
            {
                using var dll = new FakeDll("queued", status);
                Assert.Throws<InvalidOperationException>(() => Dll.RequestReload(dll.Port, new string('t', 43)));
            }
            using (var dll = new FakeDll())
                Assert.Contains("did not answer", Assert.Throws<TimeoutException>(() => Dll.RequestReload(dll.Port, new string('t', 43), timeout: 1.2)).Message);
        }

        // A started listener plus a UDP client speaking the DLL's protocol.
        private sealed class Bridge : IDisposable
        {
            public readonly Listener Listener;
            private readonly UdpClient _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            public readonly List<string> Notices = new List<string>();

            public Bridge(Func<string, string, FaceResult> generate = null)
            {
                Listener = new Listener(0, Token) { Log = _ => { } };
                Listener.Notify = text => { lock (Notices) Notices.Add(text); };
                Listener.Generate = generate ?? ((_, _) => new FaceResult { Ok = true, Preview = true });
                Listener.Run();
                _client.Client.ReceiveTimeout = 300;
            }

            public void Send(long serial, string token = Token, string action = "profile", int pid = 77, double age = 0, string person = null)
            {
                long clicked = (long)Math.Round((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - age) * 1000);
                person ??= action == "profile" ? "123" : "0";
                Raw("REGENERATEFM24 5 " + pid + " " + serial + " " + action + " " + clicked + " " + person + " " + token);
            }

            public void Raw(string packet)
            {
                var data = Encoding.ASCII.GetBytes(packet);
                _client.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, Listener.Port));
            }

            // The reply, or null after the timeout.
            public string Reply()
            {
                try
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    return Encoding.ASCII.GetString(_client.Receive(ref remote));
                }
                catch (SocketException)
                {
                    return null;
                }
            }

            public void Dispose()
            {
                _client.Dispose();
                Listener.Dispose();
            }
        }

        private static bool Eventually(Func<bool> condition)
        {
            for (int i = 0; i < 100; i++)
            {
                if (condition())
                    return true;
                Thread.Sleep(20);
            }
            return false;
        }

        [Fact]
        public void ClicksAreAuthenticatedRepliesReplayedAndBusyClicksRefused()
        {
            var started = new ManualResetEventSlim();
            var finish = new ManualResetEventSlim();
            int calls = 0;
            using var bridge = new Bridge((_, _) =>
            {
                Interlocked.Increment(ref calls);
                started.Set();
                finish.Wait(2000);
                return new FaceResult { Ok = true, Preview = true, PlayerId = "123", Prompt = "test" };
            });
            try
            {
                bridge.Send(1, token: "incorrect");
                Assert.Null(bridge.Reply());
                Assert.False(started.IsSet);
                bridge.Send(1);
                Assert.Equal("REGENERATEFM24 ACK 1 accepted", bridge.Reply());
                Assert.True(started.Wait(1000));
                // A retransmission gets the same answer and starts nothing new.
                bridge.Send(1);
                Assert.Equal("REGENERATEFM24 ACK 1 accepted", bridge.Reply());
                bridge.Send(2);
                Assert.Equal("REGENERATEFM24 ACK 2 busy", bridge.Reply());
                bridge.Send(1);
                Assert.Null(bridge.Reply());
                bridge.Send(3, action: "unknown");
                Assert.Null(bridge.Reply());
                Assert.Equal(1, calls);
            }
            finally
            {
                finish.Set();
            }
        }

        [Fact]
        public void AClickIsBoundToThePersonReadFromGameMemory()
        {
            // The DLL read Farias's ID from the clicked screen's header; even if FM24
            // now shows someone else, the request names Farias.
            var seen = new List<(string, string)>();
            var done = new ManualResetEventSlim();
            using var bridge = new Bridge((person, mode) =>
            {
                seen.Add((person, mode));
                done.Set();
                return FaceResult.Failed("The open FM24 profile is now person 2000045865, not the requested person 14240979; no image request was made.");
            });
            bridge.Send(1, person: "14240979", age: .53);
            Assert.Equal("REGENERATEFM24 ACK 1 accepted", bridge.Reply());
            Assert.True(done.Wait(2000));
            done.Reset();
            Assert.True(Eventually(() => bridge.Notices.Count == 1));
            bridge.Send(2, action: "update", person: "7458500");
            Assert.Equal("REGENERATEFM24 ACK 2 accepted", bridge.Reply());
            Assert.True(done.Wait(2000));
            Assert.Equal(new[] { ("14240979", "generate"), ("7458500", "update") }, seen);
        }

        [Fact]
        public void AProfileClickWithoutAPersonIsRefusedAndReleasesTheLock()
        {
            var requested = new ManualResetEventSlim();
            string bound = null;
            using var bridge = new Bridge((person, _) => { bound = person; requested.Set(); return new FaceResult { Ok = true, Preview = true }; });
            bridge.Send(1, person: "0");
            Assert.Equal("REGENERATEFM24 ACK 1 accepted", bridge.Reply());
            Assert.True(Eventually(() => bridge.Notices.Count == 1));
            Assert.Contains("did not report which person", bridge.Notices[0]);
            Assert.False(requested.IsSet);
            bridge.Send(2);
            Assert.Equal("REGENERATEFM24 ACK 2 accepted", bridge.Reply());
            Assert.True(requested.Wait(2000));
            Assert.Equal("123", bound);
        }

        [Fact]
        public void AnErrorAlreadyInTheProgressDialogIsNotShownAgain()
        {
            var results = new Queue<FaceResult>(new[]
            {
                new FaceResult { Error = "HTTP 500", Shown = true },
                new FaceResult { Ok = true, Warning = "reload failed", Shown = true },
                new FaceResult { Error = "HTTP 500" },
            });
            int done = 0;
            using var bridge = new Bridge((_, _) => { var result = results.Dequeue(); Interlocked.Increment(ref done); return result; });
            for (int serial = 1; serial <= 3; serial++)
            {
                bridge.Send(serial);
                Assert.Equal("REGENERATEFM24 ACK " + serial + " accepted", bridge.Reply());
                Assert.True(Eventually(() => done == serial));
                Thread.Sleep(100);
            }
            Assert.Equal(new[] { "HTTP 500" }, bridge.Notices);
        }

        [Fact]
        public void ADelayedOrFutureDatedClickIsRefused()
        {
            var requested = new ManualResetEventSlim();
            string bound = null;
            using var bridge = new Bridge((person, _) => { bound = person; requested.Set(); return new FaceResult { Ok = true, Preview = true }; });
            bridge.Send(1, age: 5);
            Assert.Equal("REGENERATEFM24 ACK 1 stale", bridge.Reply());
            // The retransmission gets the same refusal.
            bridge.Send(1, age: 5.7);
            Assert.Equal("REGENERATEFM24 ACK 1 stale", bridge.Reply());
            bridge.Send(2, action: "squad", age: 3);
            Assert.Equal("REGENERATEFM24 ACK 2 stale", bridge.Reply());
            bridge.Send(3, age: -2);
            Assert.Equal("REGENERATEFM24 ACK 3 stale", bridge.Reply());
            // A fresh click is still accepted: a refusal never takes the busy lock.
            bridge.Send(4, person: "456");
            Assert.Equal("REGENERATEFM24 ACK 4 accepted", bridge.Reply());
            Assert.True(requested.Wait(2000));
            Assert.Equal("456", bound);
        }

        [Fact]
        public void TheSquadCommandIgnoresThePersonFieldAndOldPacketsAreIgnored()
        {
            var squad = new ManualResetEventSlim();
            using var bridge = new Bridge();
            bridge.Listener.Squad = squad.Set;
            long clicked = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (string packet in new[] { "REGENERATEFM24 1 77 1 profile " + Token, "REGENERATEFM24 2 77 1 profile " + clicked + " " + Token,
                                              "REGENERATEFM24 3 77 1 profile " + clicked + " 123 " + Token })
            {
                bridge.Raw(packet);
                Assert.Null(bridge.Reply());
            }
            bridge.Send(1, action: "squad");
            Assert.Equal("REGENERATEFM24 ACK 1 accepted", bridge.Reply());
            Assert.True(squad.Wait(2000));
        }

        [Fact]
        public void TheFaceCheckAnswersThePopupAndReferencesResolve()
        {
            var asked = new List<string>();
            var resolved = new List<(uint, bool)>();
            using var bridge = new Bridge();
            bridge.Listener.HasFace = person => { asked.Add(person); return person == "123"; };
            foreach (var (serial, person, answer) in new[] { (1, "123", "REGENERATEFM24F 1 yes"), (2, "456", "REGENERATEFM24F 2 no") })
            {
                bridge.Raw("REGENERATEFM24F 1 " + serial + " " + person + " " + Token);
                Assert.Equal(answer, bridge.Reply());
            }
            foreach (string packet in new[] { "REGENERATEFM24F 1 3 123 incorrect", "REGENERATEFM24F 2 3 123 " + Token,
                                              "REGENERATEFM24F 1 3 ../1 " + Token, "REGENERATEFM24F 1 3 123 " + Token + " extra" })
            {
                bridge.Raw(packet);
                Assert.Null(bridge.Reply());
            }
            Assert.Equal(new[] { "123", "456" }, asked);
            // The macOS library sends "r<reference>" when the header shows no ID.
            bridge.Listener.Resolve = (reference, wait) => { resolved.Add((reference, wait)); return "123"; };
            bridge.Raw("REGENERATEFM24F 1 4 r16780302 " + Token);
            Assert.Equal("REGENERATEFM24F 4 yes", bridge.Reply());
            Assert.Equal((16780302u, false), resolved.Last());  // a menu never waits for the index
            string bound = null;
            var done = new ManualResetEventSlim();
            bridge.Listener.Generate = (person, _) => { bound = person; done.Set(); return new FaceResult { Ok = true, Preview = true }; };
            bridge.Send(1, person: "r16780302");
            Assert.Equal("REGENERATEFM24 ACK 1 accepted", bridge.Reply());
            Assert.True(done.Wait(2000));
            Assert.Equal(("123", (16780302u, true)), (bound, resolved.Last()));
            // A failed face check sends no answer: the DLL leaves Update face enabled.
            bridge.Listener.HasFace = _ => throw new System.IO.IOException("unreadable");
            bridge.Raw("REGENERATEFM24F 1 5 123 " + Token);
            Assert.Null(bridge.Reply());
        }

        [Fact]
        public void WithoutAResolverAReferenceClickAsksForScreenIds()
        {
            using var bridge = new Bridge((_, _) => throw new InvalidOperationException("not requested"));
            bridge.Send(1, person: "r16780302");
            Assert.Equal("REGENERATEFM24 ACK 1 accepted", bridge.Reply());
            Assert.True(Eventually(() => bridge.Notices.Count == 1));
            Assert.Contains("screen IDs", bridge.Notices[0]);
            bridge.Send(2, person: "rx");
            Assert.Null(bridge.Reply());
        }

        [Fact]
        public void AWorkerThatCantStartReleasesTheLock()
        {
            using var bridge = new Bridge();
            bridge.Listener.Start = _ => throw new InvalidOperationException("thread limit");
            bridge.Send(1);
            Assert.Equal("REGENERATEFM24 ACK 1 accepted", bridge.Reply());
            bridge.Send(2);
            Assert.Equal("REGENERATEFM24 ACK 2 accepted", bridge.Reply());
            Assert.True(Eventually(() => bridge.Notices.Count == 2));
            Assert.All(bridge.Notices, notice => Assert.Contains("thread limit", notice));
        }

        [Fact]
        public void AnOccupiedBridgePortIsAClearStartupError()
        {
            using var holder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)holder.Client.LocalEndPoint).Port;
            Assert.Contains("another companion", Assert.Throws<InvalidOperationException>(() => new Listener(port, Token)).Message);
        }
    }
}
