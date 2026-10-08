using DSharpPlus;
using DSharpPlus.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Torch.API.Managers;
using Torch.API.Session;
using Torch.Commands;
using VRage.Game;
using VRage.Game.ModAPI;

namespace SEDiscordBridge
{
    public partial class DiscordBridge
    {
        private static SEDiscordBridgePlugin Plugin;
        private readonly DiscordActivity game = new DiscordActivity();
        private string lastMessage = "";
        private ulong botId = 0;
        public DiscordConfiguration DiscordConfiguration { get; set; }
        public bool Ready { get; set; } = false;
        public static DiscordClient Discord { get; set; }

        public static int Cooldown;
        public static decimal Increment;
        public static decimal Factor;
        public static decimal CooldownNeutral;
        public static int FirstWarning;
        public static decimal MinIncrement;
        public static decimal Locked;
        public DiscordBridge(SEDiscordBridgePlugin plugin)
        {
            Plugin = plugin;

            Cooldown = plugin.Config.SimCooldown;
            Increment = (plugin.Config.StatusInterval / 1000);
            Factor = plugin.Config.SimCooldown / Increment;
            Increment = plugin.Config.SimCooldown / Increment;
            MinIncrement = 60 / (plugin.Config.StatusInterval / 1000);
            Locked = 0;
            RegisterDiscord().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private async void RunGameTask(Action obj)
        {
            if (Plugin.Torch.CurrentSession != null)
                await Plugin.Torch.InvokeAsync(obj);
            else
                await Task.Run(obj);
        }

        public void StopDiscord()
        {
            DisconnectDiscord();
        }

        private void DisconnectDiscord()
        {
            Ready = false;
            lock (_backlog)
            {
                _backlogTimer?.Dispose();
                _backlogTimer = null;
            }
            if (_backlog.Count > 0)
                SEDiscordBridgePlugin.Log.Warn($"Discord is stopped with {_backlog.Count} message(s) not delivered");
            Discord?.DisconnectAsync();
        }

        private Task RegisterDiscord()
        {
            if (Plugin.Config.BotToken == null || Plugin.Config.BotToken == string.Empty)
                return Task.CompletedTask;

            try
            {
                DiscordConfiguration = new DiscordConfiguration {
                    Token = Plugin.Config.BotToken,
                    TokenType = TokenType.Bot,
                    AutoReconnect = true,
                    HttpTimeout = TimeSpan.FromSeconds(10),
                    MessageCacheSize = 2048,
                    LargeThreshold = 250,
                    Intents = DiscordIntents.AllUnprivileged | DiscordIntents.MessageContents,
                };

                Discord = new DiscordClient(DiscordConfiguration);
            }
            catch (Exception) { }

            Discord.MessageCreated += Discord_MessageCreated;
            Discord.SocketClosed += Discord_SocketError;
            Discord.Zombied += Discord_Zombied;

            Discord.Ready += async (c, e) =>
            {
                Ready = true;
                await Task.CompletedTask;
            };

            // AutoReconnect usually resumes the old session: Discord then sends RESUMED, not READY
            Discord.Resumed += (c, e) =>
            {
                Ready = true;
                return Task.CompletedTask;
            };

            // handlers first, so the READY of this connect is not missed
            Discord.ConnectAsync().ContinueWith(t =>
                SEDiscordBridgePlugin.Log.Warn(t.Exception?.GetBaseException(), "Discord connect failed"),
                TaskContinuationOptions.OnlyOnFaulted);

            return Task.CompletedTask;
        }

        public void SendStatus(string status, UserStatus userStatus)
        {
            if (Ready && status?.Length > 0)
            {
                game.Name = status;
                Discord.UpdateStatusAsync(game, userStatus);
            }
        }

        private Task Discord_SocketError(DiscordClient discord, DSharpPlus.EventArgs.SocketCloseEventArgs e)
        {
            Ready = false;

            SEDiscordBridgePlugin.Log.Warn($"SocketClose Event: code {e.CloseCode} {e.CloseMessage}");

            return Task.CompletedTask;
        }

        private Task Discord_Zombied(DiscordClient discord, DSharpPlus.EventArgs.ZombiedEventArgs e)
        {
            Discord.ReconnectAsync();

            return Task.CompletedTask;
        }

        public static async void SendDiscordMessageStatic(string message) {

            string channelToPostIn = Plugin.Config.ChatChannelId;
            if (channelToPostIn == string.Empty) {
                channelToPostIn = Plugin.Config.StatusChannelId;
            }

            if(channelToPostIn == string.Empty) {
                SEDiscordBridgePlugin.Log.Error("StatusChannelID or ChatChannelID MUST have a value. Cannot use SendDiscordMessageStatic.");
                return;
            }

            await Discord.SendMessageAsync(Discord.GetChannelAsync(ulong.Parse(channelToPostIn)).Result, message);
        }

        public static async void SendDiscordMessageStatic(string message, string channelID) {
            await Discord.SendMessageAsync(Discord.GetChannelAsync(ulong.Parse(channelID)).Result, message);
        }

        public void SendSimMessage(string msg)
        {
            try
            {
                if (Ready && Plugin.Config.SimChannel.Length > 0)
                {
                    DiscordChannel chann = Discord.GetChannelAsync(ulong.Parse(Plugin.Config.SimChannel)).Result;
                    //mention
                    msg = MentionNameToID(msg, chann);
                    msg = Plugin.Config.SimMessage.Replace("{ts}", TimeZone.CurrentTimeZone.ToLocalTime(DateTime.Now).ToString());
                    botId = Discord.SendMessageAsync(chann, msg.Replace("/n", "\n")).Result.Author.Id;
                }
            }
            catch (Exception e)
            {
                SEDiscordBridgePlugin.Log.Error(e);
            }
        }

        // How long a message waits for the client to be ready (a reconnect of the gateway) before it is sent anyway.
        private const int ReadyWaitMs = 15000;

        // Pauses before the second and the third attempt of a send that timed out or hit a network or server error.
        private static readonly int[] RetryDelaysMs = { 2000, 5000 };

        /// <summary>
        /// One message to one channel. It goes by REST, which does not need the gateway: a message written while the
        /// gateway reconnects (Ready false for a few seconds after a close) used to be dropped without a word, now it
        /// waits up to <see cref="ReadyWaitMs"/> for the client and is sent. A timeout (TaskCanceledException after
        /// HttpTimeout), a network error or a Discord server error is tried again - three attempts - instead of the
        /// message being lost; what still fails throws to the caller, unwrapped. Every attempt carries the same
        /// <paramref name="nonce"/>: Discord may have taken a send that timed out, and the next attempt put the line
        /// into the channel a second time - with the nonce it gives back the message it has (<see cref="OnceSend"/>).
        /// Returns the bot's id. Blocks: thread pool only, never the game thread.
        /// </summary>
        private ulong SendToChannel(ulong channelId, Func<DiscordChannel, string> makeText, string nonce)
        {
            for (var waited = 0; !Ready && waited < ReadyWaitMs; waited += 500)
                Thread.Sleep(500);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var channel = Discord.GetChannelAsync(channelId).GetAwaiter().GetResult();
                    return SendOnce(channelId, makeText(channel).Replace("/n", "\n"), nonce);
                }
                catch (Exception e) when (attempt < RetryDelaysMs.Length && Transient(e))
                {
                    SEDiscordBridgePlugin.Log.Warn($"Discord send to channel {channelId}: {Unwrap(e).GetType().Name}, trying again in {RetryDelaysMs[attempt] / 1000} s");
                    Thread.Sleep(RetryDelaysMs[attempt]);
                }
            }
        }

        private static bool _noNonceSaid;

        /// <summary>One send under a nonce; the bot's id.</summary>
        private static ulong SendOnce(ulong channelId, string text, string nonce)
        {
            if (OnceSend.Available) return OnceSend.SendAsync(Discord, channelId, text, nonce).GetAwaiter().GetResult();
            if (!_noNonceSaid)
            {
                _noNonceSaid = true;
                SEDiscordBridgePlugin.Log.Warn("This DSharpPlus is not the one the nonce send was written for: a send that timed out and is sent again may come out twice");
            }
            var channel = Discord.GetChannelAsync(channelId).GetAwaiter().GetResult();
            return Discord.SendMessageAsync(channel, text).GetAwaiter().GetResult().Author.Id;
        }

        // What Discord did not take in all the attempts: kept up to five minutes and tried again (Backlog).
        private const int BacklogRetryMs = 10000;
        private readonly Backlog _backlog = new Backlog();
        private Timer _backlogTimer;
        private int _flushing;

        /// <summary>
        /// One message to one channel that must not be lost to a few minutes without Discord. Sent at once (waiting for
        /// the client, three attempts - <see cref="SendToChannel"/>); when Discord still gives no answer, the text is
        /// kept and delivered later, joined with the others kept for the channel. A channel with messages waiting gets
        /// the new one behind them at once: no half a minute of attempts for each, and the order stays.
        /// Blocks: thread pool only.
        /// </summary>
        private void Deliver(ulong channelId, Func<DiscordChannel, string> makeText, string what)
        {
            var at = DateTime.UtcNow;
            string text = null;
            string sent = null;
            if (!_backlog.Waiting(channelId))
            {
                var nonce = Backlog.NewNonce();
                try
                {
                    botId = SendToChannel(channelId, channel => text = makeText(channel), nonce);
                    return;
                }
                catch (Exception e) when (Transient(e))
                {
                    // the text may be there already: it is sent again as it is, under the same nonce
                    if (text != null) sent = nonce;
                    SEDiscordBridgePlugin.Log.Warn($"{what}: no answer from Discord ({Unwrap(e).GetType().Name}), kept for {Backlog.KeepMs / 60000} minutes");
                }
                catch (Exception e)
                {
                    LogSendFailure(what, e, text ?? "");
                    return;
                }
            }
            // the text without asking Discord: the channel from the client's cache (none - mentions stay as written)
            if (text == null)
                text = makeText(Discord.Guilds.Values.Select(g => g.GetChannel(channelId)).FirstOrDefault(c => c != null));
            _backlog.Add(channelId, at, text.Replace("/n", "\n"), sent);
            lock (_backlog)
                if (_backlogTimer == null)
                    _backlogTimer = new Timer(_ => FlushBacklog(), null, BacklogRetryMs, BacklogRetryMs);
        }

        private void FlushBacklog()
        {
            // one pass at a time: a pass waits for Discord's timeout
            if (Interlocked.Exchange(ref _flushing, 1) == 1) return;
            try
            {
                if (_backlog.Count == 0 || Discord == null) return;
                var delivered = _backlog.Flush(DateTime.UtcNow,
                    (channelId, text, nonce) => botId = SendOnce(channelId, text, nonce),
                    Transient,
                    (channelId, text, why) =>
                    {
                        SEDiscordBridgePlugin.Log.Error($"Message to channel {channelId}: not sent ({why})");
                        SEDiscordBridgePlugin.Log.Warn($"Message: {text}");
                    });
                if (delivered > 0)
                    SEDiscordBridgePlugin.Log.Info($"Discord answers again: {delivered} kept message(s) delivered, {_backlog.Count} waiting");
            }
            catch (Exception e)
            {
                SEDiscordBridgePlugin.Log.Error(e, "Discord backlog");
            }
            finally
            {
                Interlocked.Exchange(ref _flushing, 0);
            }
        }

        private static Exception Unwrap(Exception e) => (e as AggregateException)?.GetBaseException() ?? e;

        private static bool Transient(Exception e)
        {
            e = Unwrap(e);
            return e is TaskCanceledException || e is System.Net.Http.HttpRequestException ||
                   e is DSharpPlus.Exceptions.ServerErrorException || e is DSharpPlus.Exceptions.RateLimitException;
        }

        private static void LogSendFailure(string what, Exception e, string text)
        {
            e = Unwrap(e);
            if (e is DSharpPlus.Exceptions.NotFoundException)
                SEDiscordBridgePlugin.Log.Error($"{what}: channel not found");
            else
                SEDiscordBridgePlugin.Log.Error($"{what}: not sent ({e.GetType().Name}: {e.Message})");
            SEDiscordBridgePlugin.Log.Warn($"Message: {text}");
        }

        public Task SendChatMessage(string user, string msg)
        {
            if (lastMessage.Equals(user + msg) || Discord == null || Plugin.Config.ChatChannelId.Length == 0)
                return Task.CompletedTask;

            foreach (var chanID in Plugin.Config.ChatChannelId.Split(' '))
            {
                // {ts} is when it was written, also for a message delivered later
                var written = TimeZone.CurrentTimeZone.ToLocalTime(DateTime.Now).ToString();
                try
                {
                    Deliver(ulong.Parse(chanID), chann =>
                    {
                        var text = MentionNameToID(msg, chann);
                        if (user != null)
                            text = Plugin.Config.Format.Replace("{msg}", text).Replace("{p}", user).Replace("{ts}", written);
                        return text;
                    }, $"Chat message to channel {chanID}");
                }
                catch (Exception e)
                {
                    LogSendFailure($"Chat message to channel {chanID}", e, msg);
                }
            }
            return Task.CompletedTask;
        }

        public void SendFacChatMessage(string user, string msg, string facName)
        {
            if (Discord == null) return;
            foreach (var chId in Plugin.Config.FactionChannels.Where(c => c.Split(':')[0].Equals(facName)))
            {
                var written = TimeZone.CurrentTimeZone.ToLocalTime(DateTime.Now).ToString();
                try
                {
                    Deliver(ulong.Parse(chId.Split(':')[1]), chann =>
                    {
                        var text = MentionNameToID(msg, chann);
                        if (user != null)
                            text = Plugin.Config.FacFormat.Replace("{msg}", text).Replace("{p}", user).Replace("{ts}", written);
                        return text;
                    }, $"Faction message to {chId}");
                }
                catch (Exception e)
                {
                    LogSendFailure($"Faction message to {chId}", e, msg);
                }
            }
        }

        public void SendStatusMessage(string user, string msg, Torch.API.IPlayer obj = null)
        {
            if (Discord == null || Plugin.Config.StatusChannelId.Length == 0) return;

            if (user != null)
            {
                if (user.StartsWith("ID:"))
                    return;

                if (obj != null && Plugin.Config.DisplaySteamId)
                    user = $"{user} ({obj.SteamId})";

                msg = msg.Replace("{p}", user).Replace("{ts}", TimeZone.CurrentTimeZone.ToLocalTime(DateTime.Now).ToString());
            }

            try
            {
                Deliver(ulong.Parse(Plugin.Config.StatusChannelId), _ => msg, "Status message");
            }
            catch (Exception e)
            {
                LogSendFailure("Status message", e, msg);
            }
        }

        public string GetName(ulong userID) {
            string discordname;
            var guilds = Discord.Guilds;

            foreach (var guildID in guilds) {
                var Guild = Discord.GetGuildAsync(guildID.Key).Result;
                return discordname = Guild.GetMemberAsync(userID).Result.DisplayName;
            }

            return null;
        }

        private Task Discord_MessageCreated(DiscordClient discord, DSharpPlus.EventArgs.MessageCreateEventArgs e)
        {
            bool cmdConditionMatch = false;
            dynamic cmdPrefixes = Plugin.Config.CommandPrefix;
            string matchedPrefix = "";
            cmdPrefixes = cmdPrefixes.Split();

            if (!e.Author.IsBot || (!botId.Equals(e.Author.Id) && Plugin.Config.BotToGame))
            {
                string comChannelId = Plugin.Config.CommandChannelId;
                if (!string.IsNullOrEmpty(comChannelId))
                {
                    foreach (string prefix in cmdPrefixes) {
                        if (Plugin.Config.CommandChannelId.Contains(e.Channel.Id.ToString()) && e.Message.Content.StartsWith(prefix)) {
                            cmdConditionMatch = true;
                            matchedPrefix = prefix;
                        }
                    }

                    //execute commands
                    if (cmdConditionMatch)
                    {
                        var cmdArgs = e.Message.Content.Substring(matchedPrefix.Length);
                        var cmd = cmdArgs.Split(' ')[0];

                        // Check for permission
                        if (Plugin.Config.CommandPerms.Count() > 0)
                        {
                            var userId = e.Author.Id.ToString();
                            bool hasRolePerm = e.Guild.GetMemberAsync(e.Author.Id).Result.Roles.Where(r => Plugin.Config.CommandPerms.Where(c => c.Split(':')[0].Equals(r.Id.ToString())).Any()).Any();

                            if (Plugin.Config.CommandPerms.Where(c =>
                            {
                                if (!hasRolePerm && !c.Split(':')[0].Equals(userId))
                                    return true;
                                else
                                if ((c.Split(':')[0].Equals(userId) || hasRolePerm) && (c.Split(':')[1].Equals(cmd) || c.Split(':')[1].Equals("*")))
                                    return false;

                                return true;
                            }).Any())
                            {
                                SendCmdResponse($"No permission for command: {cmd}", e.Channel, DiscordColor.Red, cmd);
                                return Task.CompletedTask;
                            }
                        }

                        if (Plugin.Torch.CurrentSession?.State == TorchSessionState.Loaded)
                        {
                            var manager = Plugin.Torch.CurrentSession.Managers.GetManager<CommandManager>();
                            var command = manager.Commands.GetCommand(cmdArgs, out string argText);

                            if (command == null)
                                SendCmdResponse($"Command not found: {cmdArgs}", e.Channel, DiscordColor.Red, cmd);
                            else
                            {
                                var cmdPath = string.Join(".", command.Path);
                                var splitArgs = Regex.Matches(argText, "(\"[^\"]+\"|\\S+)").Cast<Match>().Select(x => x.ToString().Replace("\"", "")).ToList();
                                SEDiscordBridgePlugin.Log.Trace($"Invoking {cmdPath} for server.");

                                var context = new SEDBCommandHandler(Plugin.Torch, command.Plugin, Sync.MyId, argText, splitArgs)
                                {
                                    ResponeChannel = e.Channel
                                };
                                context.OnResponse += OnCommandResponse;
                                var invokeSuccess = false;
                                Plugin.Torch.InvokeBlocking(() => invokeSuccess = command.TryInvoke(context));
                                SEDiscordBridgePlugin.Log.Debug($"invokeSuccess {invokeSuccess}");
                                if (!invokeSuccess)
                                    SendCmdResponse($"Error executing command: {cmdArgs}", e.Channel, DiscordColor.Red, cmd);

                                SEDiscordBridgePlugin.Log.Info($"Server ran command '{cmdArgs}'");
                            }
                        }
                        else
                            SendCmdResponse("Error: Server is not running.", e.Channel, DiscordColor.Red, cmd);

                        return Task.CompletedTask;
                    }
                }

                //send to global
                if (Plugin.Config.ChatChannelId.Contains(e.Channel.Id.ToString()))
                {
                    string sender = Plugin.Config.ServerName;

                    if (!Plugin.Config.AsServer)
                    {
                        if (Plugin.Config.UseNicks)
                        {
                            var user = e.Guild.GetMemberAsync(e.Author.Id).Result;
                            sender = user.Nickname;
                            if (string.IsNullOrWhiteSpace(sender))
                            {
                                sender = user.Username;
                            }
                        }
                        else
                            sender = e.Guild.GetMemberAsync(e.Author.Id).Result.Username;
                    }

                    var manager = Plugin.Torch?.CurrentSession?.Managers?.GetManager<IChatManagerServer>();
                    if (manager != null) {
                        var dSender = Plugin.Config.Format2.Replace("{p}", sender);
                        var msg = MentionIDToName(e.Message);
                        lastMessage = dSender + msg;
                    	manager.SendMessageAsOther(dSender, msg,
                        	typeof(MyFontEnum).GetFields().Select(x => x.Name).Where(x => x.Equals(Plugin.Config.GlobalColor)).First());
                    }
                }

                //send to faction
                var channelIds = Plugin.Config.FactionChannels.Where(c => e.Channel.Id.Equals(ulong.Parse(c.Split(':')[1])));
                if (channelIds.Count() > 0)
                {
                    foreach (var chId in channelIds)
                    {
                        IEnumerable<IMyFaction> facs = MySession.Static.Factions.Factions.Values.Where(f => f.Name.Equals(chId.Split(':')[0]));
                        if (facs.Count() > 0)
                        {
                            IMyFaction fac = facs.First();
                            foreach (MyFactionMember mb in fac.Members.Values)
                            {
                                if (!MySession.Static.Players.GetOnlinePlayers().Any(p => p.Identity.IdentityId.Equals(mb.PlayerId)))
                                    continue;

                                ulong steamid = MySession.Static.Players.TryGetSteamId(mb.PlayerId);
                                string sender = Plugin.Config.ServerName;
                                if (!Plugin.Config.AsServer)
                                {
                                    if (Plugin.Config.UseNicks)
                                    {
                                        var user = e.Guild.GetMemberAsync(e.Author.Id).Result;
                                        sender = user.Nickname;
                                        if (string.IsNullOrWhiteSpace(sender))
                                        {
                                            sender = user.Username;
                                        }
                                    }
                                    else
                                        sender = e.Author.Username;
                                }

                                var manager = Plugin.Torch?.CurrentSession?.Managers?.GetManager<IChatManagerServer>();
                                if (manager != null)
                                {
                                    var dSender = Plugin.Config.FacFormat2.Replace("{p}", sender);
                                    var msg = MentionIDToName(e.Message);
                                    lastMessage = dSender + msg;
                                	manager.SendMessageAsOther(dSender, msg,
                                    	typeof(MyFontEnum).GetFields().Select(x => x.Name).Where(x => x.Equals(Plugin.Config.FacColor)).First(), steamid);
                                }
                            }
                        }
                    }
                }
            }
            return Task.CompletedTask;
        }

        private void SendCmdResponse(string response, DiscordChannel chann, DiscordColor color, string command)
        {
            if (Plugin.Config.Embed) {
                DiscordEmbed discordEmbed = new DiscordEmbedBuilder() {
                    Description = response,
                    Color = color,
                    Title = string.IsNullOrEmpty(command) ? null : $"Command: {command}"
                };
                DiscordMessage dms = Discord.SendMessageAsync(chann, "", discordEmbed).Result;

                botId = dms.Author.Id;
                if (Plugin.Config.RemoveResponse > 0)
                    Task.Delay(Plugin.Config.RemoveResponse * 1000).ContinueWith(t => dms?.DeleteAsync());
            }
            else {
                DiscordMessage dms = Discord.SendMessageAsync(chann, response).Result;
                botId = dms.Author.Id;
                if (Plugin.Config.RemoveResponse > 0)
                    Task.Delay(Plugin.Config.RemoveResponse * 1000).ContinueWith(t => dms?.DeleteAsync());
            }
        }

        private string MentionNameToID(string msg, DiscordChannel chann)
        {
            try
            {
                var parts = msg.Split(' ');
                foreach (string part in parts)
                {
                    if (part.Length > 2)
                    {
                        if (part.StartsWith("@"))
                        {
                            string name = Regex.Replace(part.Substring(1), @"[,#]", "");
                            var roleDictionary = chann.Guild.Roles;
                            dynamic roleToMention = null;

                            foreach (var role in roleDictionary) {
                                if (role.Value.Name == name)
                                    roleToMention = role.Value;
                            }

                            if (roleToMention != null) {
                                msg = msg.Replace(part, roleToMention.Mention);
                                continue;
                            }

                            if (string.Compare(name, "everyone", true) == 0 && !Plugin.Config.MentEveryone)
                            {
                                msg = msg.Replace(part, part.Substring(1));
                                continue;
                            }

                            if (string.Compare(name, "here", true) == 0 && !Plugin.Config.MentEveryone) {
                                msg = msg.Replace(part, part.Substring(1));
                                continue;
                            }

                            try
                            {
                                var members = chann.Guild.GetAllMembersAsync().Result;

                                if (!Plugin.Config.MentOthers)
                                    continue;

                                var memberByNickname = members.FirstOrDefault((u) => string.Compare(u.Nickname, name, true) == 0);
                                if (memberByNickname != null)
                                {
                                    msg = msg.Replace(part, $"<@{memberByNickname.Id}>");
                                    continue;
                                }

                                var memberByUsername = members.FirstOrDefault((u) => string.Compare(u.Username, name, true) == 0);
                                if (memberByUsername != null)
                                {
                                    msg = msg.Replace(part, $"<@{memberByUsername.Id}>");
                                    continue;
                                }
                            }
                            catch (Exception e)
                            {
                                SEDiscordBridgePlugin.Log.Warn(e, "Error on convert a member id to name on mention other players.");
                                continue;
                            }
                        }

                        var emojis = chann.Guild.Emojis;
                        if (part.StartsWith(":") && part.EndsWith(":") && emojis.Any(e => string.Compare(e.Value.GetDiscordName(), part, true) == 0))
                            msg = msg.Replace(part, $"<{part}{emojis.Where(e => string.Compare(e.Value.GetDiscordName(), part, true) == 0).First().Key}>");
                    }
                }
            }
            catch (Exception e)
            {
                SEDiscordBridgePlugin.Log.Warn(e, "Error on convert a member id to name on mention other players.");
            }
            return msg;
        }

        private string MentionIDToName(DiscordMessage ddMsg)
        {
            string msg = ddMsg.Content;
            var parts = msg.Split(' ');
            foreach (string part in parts)
            {
                if (part.StartsWith("<@!") && part.EndsWith(">"))
                {
                    try
                    {
                        ulong id = ulong.Parse(part.Substring(3, part.Length - 4));

                        var name = Discord.GetUserAsync(id).Result.Username;
                        if (Plugin.Config.UseNicks)
                            name = ddMsg.Channel.Guild.GetMemberAsync(id).Result.Nickname;

                        msg = msg.Replace(part, "@" + name);
                    }
                    catch (FormatException) { }
                }
                if (part.StartsWith("<:") && part.EndsWith(">"))
                {
                    string id = part.Substring(2, part.Length - 3);
                    msg = msg.Replace(part, ":" + id.Split(':')[0] + ":");
                }
            }
            return msg;
        }

        private void OnCommandResponse(DiscordChannel channel, string message, string sender = "Server", string font = "White")
        {
            SEDiscordBridgePlugin.Log.Debug($"response length {message.Length}");
            if (message.Length > 0)
            {
                message = message.Replace("_", "\\_")
                    .Replace("*", "\\*")
                    .Replace("~", "\\~");
                if (Plugin.Config.StripGPS)
                    message = Regex.Replace(message, @"@\ ?[0-9E+.-]+,[0-9E+.-]+,[0-9E+.-]+", "", RegexOptions.Multiline);

                const int chunkSize = 2000 - 1; // Remove 1 just ensure everything is ok

                if (message.Length <= chunkSize)
                    SendCmdResponse(message, channel, DiscordColor.Green, null);
                else
                {
                    var index = 0;
                    do
                    {
                        SEDiscordBridgePlugin.Log.Debug($"while iteration index {index}");

                        /* if remaining part of message is small enough then just output it. */
                        if (index + chunkSize >= message.Length)
                        {
                            SendCmdResponse(message.Substring(index), channel, DiscordColor.Green, null);
                            break;
                        }

                        var chunk = message.Substring(index, chunkSize);
                        var newLineIndex = chunk.LastIndexOf("\n");
                        SEDiscordBridgePlugin.Log.Debug($"while iteration newLineIndex {newLineIndex}");

                        SendCmdResponse(chunk.Substring(0, newLineIndex), channel, DiscordColor.Green, null);
                        index += newLineIndex + 1;

                    } while (index < message.Length);
                }
            }
        }
    }
}