using System;
using System.Reflection;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Net;
using Newtonsoft.Json.Linq;

namespace SEDiscordBridge
{
    /// <summary>
    /// A message Discord takes once however many times it is sent. A send that timed out (no answer in HttpTimeout)
    /// may have been taken all the same: the second attempt then put the same chat line into the channel again.
    /// Discord's own guard against that is a nonce with "enforce_nonce": a message of the same author with a nonce
    /// seen in the last few minutes is not created again, the first one is returned. DSharpPlus 4.5.2 has no nonce
    /// in its sends, so the request is made here and handed to its REST client (its rate limits and timeout stay)
    /// through its internals.
    /// </summary>
    internal static class OnceSend
    {
        private const string Route = "/channels/:channel_id/messages";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly PropertyInfo ApiClient = typeof(BaseDiscordClient).GetProperty("ApiClient", Any);
        private static readonly PropertyInfo Rest = typeof(DiscordApiClient).GetProperty("_rest", Any);
        private static readonly MethodInfo GetBucket = Rest?.PropertyType.GetMethod("GetBucket", Any);
        private static readonly MethodInfo DoRequest = typeof(DiscordApiClient).GetMethod("DoRequestAsync", Any);

        /// <summary>Whether this DSharpPlus has what the send needs (another version may not: the plain send is used then).</summary>
        public static bool Available =>
            ApiClient != null && Rest != null && GetBucket != null && DoRequest != null && DoRequest.GetParameters().Length == 8 &&
            typeof(Task<RestResponse>).IsAssignableFrom(DoRequest.ReturnType);

        /// <summary>Sends the text with the nonce; the id of the author (the bot) of the message Discord has for it.</summary>
        public static async Task<ulong> SendAsync(DiscordClient client, ulong channelId, string text, string nonce)
        {
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("Message content must not be empty.");
            if (text.Length > Backlog.MessageLimit) throw new ArgumentException("Message content length cannot exceed 2000 characters.");

            var payload = new JObject { ["content"] = text, ["tts"] = false, ["nonce"] = nonce, ["enforce_nonce"] = true }.ToString(Newtonsoft.Json.Formatting.None);
            var api = ApiClient.GetValue(client);
            var rest = Rest.GetValue(api);
            var args = new object[] { RestRequestMethod.POST, Route, new { channel_id = channelId }, null };
            var bucket = GetBucket.Invoke(rest, args);
            var uri = new Uri("https://discord.com/api/v10" + (string)args[3]);
            var response = await ((Task<RestResponse>)DoRequest.Invoke(api, new object[] { client, bucket, uri, RestRequestMethod.POST, Route, null, payload, null }))
                .ConfigureAwait(false);
            return (ulong?)JObject.Parse(response.Response)["author"]?["id"] ?? client.CurrentUser?.Id ?? 0;
        }
    }
}
