using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SEDiscordBridge
{
    /// <summary>
    /// Messages Discord did not take (no answer from its REST for all the attempts), kept per channel and tried again:
    /// a few minutes without Discord used to lose every chat line written in them. A message waits no longer than
    /// <see cref="KeepMs"/>, then it is dropped; what waits goes out in the order written, joined into as few Discord
    /// messages as fit. No Discord in here: the caller gives the send, so the rules are tested without one.
    /// </summary>
    public sealed class Backlog
    {
        /// <summary>How long an undelivered message is kept.</summary>
        public const int KeepMs = 5 * 60 * 1000;

        /// <summary>Discord's limit of one message.</summary>
        public const int MessageLimit = 2000;

        private sealed class Pending
        {
            public DateTime At;
            public string Text;
        }

        private readonly Dictionary<ulong, List<Pending>> _waiting = new Dictionary<ulong, List<Pending>>();

        /// <summary>Whether the channel has messages waiting: a new one then goes behind them, not past them.</summary>
        public bool Waiting(ulong channelId)
        {
            lock (_waiting) return _waiting.TryGetValue(channelId, out var list) && list.Count > 0;
        }

        public int Count
        {
            get { lock (_waiting) return _waiting.Values.Sum(l => l.Count); }
        }

        public void Add(ulong channelId, DateTime at, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (text.Length > MessageLimit) text = text.Substring(0, MessageLimit);
            lock (_waiting)
            {
                if (!_waiting.TryGetValue(channelId, out var list)) _waiting[channelId] = list = new List<Pending>();
                list.Add(new Pending { At = at, Text = text });
            }
        }

        /// <summary>
        /// Tries to deliver what waits. <paramref name="send"/> sends one text to a channel and throws when it does
        /// not go; <paramref name="transient"/> tells an error worth waiting out (the messages stay, and the pass ends:
        /// Discord is still away) from a final one (the messages are dropped). Returns what was delivered; too old
        /// and refused messages come back through <paramref name="dropped"/> (channel, text, why).
        /// </summary>
        public int Flush(DateTime now, Action<ulong, string> send, Func<Exception, bool> transient, Action<ulong, string, string> dropped)
        {
            var delivered = 0;
            ulong[] channels;
            lock (_waiting) channels = _waiting.Keys.ToArray();
            foreach (var channelId in channels)
            {
                while (true)
                {
                    List<Pending> batch;
                    var old = new List<Pending>();
                    lock (_waiting)
                    {
                        if (!_waiting.TryGetValue(channelId, out var list)) break;
                        old.AddRange(list.Where(p => (now - p.At).TotalMilliseconds > KeepMs));
                        list.RemoveAll(old.Contains);
                        if (list.Count == 0)
                        {
                            _waiting.Remove(channelId);
                            batch = null;
                        }
                        else
                        {
                            // in the order written: two sends that failed side by side came in out of it
                            list.Sort((a, b) => a.At.CompareTo(b.At));
                            batch = new List<Pending>();
                            var length = 0;
                            foreach (var p in list)
                            {
                                var more = p.Text.Length + (batch.Count > 0 ? 1 : 0);
                                if (batch.Count > 0 && length + more > MessageLimit) break;
                                batch.Add(p);
                                length += more;
                            }
                        }
                    }
                    foreach (var p in old) dropped(channelId, p.Text, "not delivered in " + KeepMs / 60000 + " minutes");
                    if (batch == null) break;

                    var text = new StringBuilder();
                    foreach (var p in batch) text.Append(text.Length > 0 ? "\n" : "").Append(p.Text);
                    try
                    {
                        send(channelId, text.ToString());
                    }
                    catch (Exception e) when (transient(e))
                    {
                        return delivered;       // still no Discord: everything stays for the next pass
                    }
                    catch (Exception e)
                    {
                        lock (_waiting)
                            if (_waiting.TryGetValue(channelId, out var list)) list.RemoveAll(batch.Contains);
                        foreach (var p in batch) dropped(channelId, p.Text, e.GetType().Name + ": " + e.Message);
                        continue;
                    }
                    lock (_waiting)
                        if (_waiting.TryGetValue(channelId, out var list)) list.RemoveAll(batch.Contains);
                    delivered += batch.Count;
                }
            }
            return delivered;
        }
    }
}
