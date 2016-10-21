﻿using Discord.Commands;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Discord;
using NadekoBot.Services;
using NadekoBot.Attributes;
using Discord.WebSocket;
using NadekoBot.Services.Database.Models;
using System.Linq;
using NadekoBot.Services.Database;
using NadekoBot.Extensions;

namespace NadekoBot.Modules.ClashOfClans
{
    [NadekoModule("ClashOfClans", ",")]
    public class ClashOfClans : DiscordModule
    {
        public static ConcurrentDictionary<ulong, List<ClashWar>> ClashWars { get; set; } = new ConcurrentDictionary<ulong, List<ClashWar>>();

        static ClashOfClans()
        {
            using (var uow = DbHandler.UnitOfWork())
            {
                ClashWars = new ConcurrentDictionary<ulong, List<ClashWar>>(
                    uow.ClashOfClans
                        .GetAllWars()
                        .Select(cw =>
                        {
                            cw.Channel = NadekoBot.Client.GetGuild(cw.GuildId)
                                                         ?.GetTextChannel(cw.ChannelId);
                            return cw;
                        })
                        .Where(cw => cw?.Channel != null)
                        .GroupBy(cw => cw.GuildId)
                        .ToDictionary(g => g.Key, g => g.ToList()));
            }
        }

        public ClashOfClans() : base()
        {

        }
        
        private static async Task CheckWar(TimeSpan callExpire, ClashWar war)
        {
            var Bases = war.Bases;
            for (var i = 0; i < Bases.Count; i++)
            {
                if (Bases[i].CallUser == null) continue;
                if (!Bases[i].BaseDestroyed && DateTime.UtcNow - Bases[i].TimeAdded >= callExpire)
                {
                    Bases[i] = null;
                    try { await war.Channel.SendMessageAsync($"❗🔰**Claim from @{Bases[i].CallUser} for a war against {war.ShortPrint()} has expired.**").ConfigureAwait(false); } catch { }
            }
            }
        }

        [NadekoCommand, Usage, Description, Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task CreateWar(int size, [Remainder] string enemyClan = null)
        {
            var channel = (SocketTextChannel)Context.Channel;

            if (!(Context.User as IGuildUser).GuildPermissions.ManageChannels)
                return;

            if (string.IsNullOrWhiteSpace(enemyClan))
                return;

            if (size < 10 || size > 50 || size % 5 != 0)
            {
                await channel.SendMessageAsync("💢🔰 Not a Valid war size").ConfigureAwait(false);
                return;
            }
            List<ClashWar> wars;
            if (!ClashWars.TryGetValue(channel.Guild.Id, out wars))
            {
                wars = new List<ClashWar>();
                if (!ClashWars.TryAdd(channel.Guild.Id, wars))
                    return;
            }


            var cw = await CreateWar(enemyClan, size, channel.Guild.Id, Context.Channel.Id);

            wars.Add(cw);
            await channel.SendMessageAsync($"❗🔰**CREATED CLAN WAR AGAINST {cw.ShortPrint()}**").ConfigureAwait(false);
        }

        [NadekoCommand, Usage, Description, Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task StartWar([Remainder] string number = null)
        {
            var channel = (SocketTextChannel)Context.Channel;

            int num = 0;
            int.TryParse(number, out num);

            var warsInfo = GetWarInfo((SocketGuild)Context.Guild, num);
            if (warsInfo == null)
            {
                await channel.SendMessageAsync("💢🔰 **That war does not exist.**").ConfigureAwait(false);
                return;
            }
            var war = warsInfo.Item1[warsInfo.Item2];
            try
            {
                war.Start();
                await channel.SendMessageAsync($"🔰**STARTED WAR AGAINST {war.ShortPrint()}**").ConfigureAwait(false);
            }
            catch
            {
                await channel.SendMessageAsync($"🔰**WAR AGAINST {war.ShortPrint()} HAS ALREADY STARTED**").ConfigureAwait(false);
            }
            SaveWar(war);
        }

        [NadekoCommand, Usage, Description, Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task ListWar([Remainder] string number = null)
        {
            var channel = (SocketTextChannel)Context.Channel;

            // if number is null, print all wars in a short way
            if (string.IsNullOrWhiteSpace(number))
            {
                //check if there are any wars
                List<ClashWar> wars = null;
                ClashWars.TryGetValue(channel.Guild.Id, out wars);
                if (wars == null || wars.Count == 0)
                {
                    await channel.SendMessageAsync("🔰 **No active wars.**").ConfigureAwait(false);
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("🔰 **LIST OF ACTIVE WARS**");
                sb.AppendLine("**-------------------------**");
                for (var i = 0; i < wars.Count; i++)
                {
                    sb.AppendLine($"**#{i + 1}.**  `Enemy:` **{wars[i].EnemyClan}**");
                    sb.AppendLine($"\t\t`Size:` **{wars[i].Size} v {wars[i].Size}**");
                    sb.AppendLine("**-------------------------**");
                }
                await channel.SendMessageAsync(sb.ToString()).ConfigureAwait(false);
                return;

            }
            var num = 0;
            int.TryParse(number, out num);
            //if number is not null, print the war needed
            var warsInfo = GetWarInfo((SocketGuild)Context.Guild, num);
            if (warsInfo == null)
            {
                await channel.SendMessageAsync("💢🔰 **That war does not exist.**").ConfigureAwait(false);
                return;
            }
            await channel.SendMessageAsync(warsInfo.Item1[warsInfo.Item2].ToPrettyString()).ConfigureAwait(false);
        }

        [NadekoCommand, Usage, Description, Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task Claim(int number, int baseNumber, [Remainder] string other_name = null)
        {
            var channel = (SocketTextChannel)Context.Channel;

            var warsInfo = GetWarInfo((SocketGuild)Context.Guild, number);
            if (warsInfo == null || warsInfo.Item1.Count == 0)
            {
                await channel.SendMessageAsync("💢🔰 **That war does not exist.**").ConfigureAwait(false);
                return;
            }
            var usr =
                string.IsNullOrWhiteSpace(other_name) ?
                Context.User.Username :
                other_name;
            try
            {
                var war = warsInfo.Item1[warsInfo.Item2];
                war.Call(usr, baseNumber - 1);
                SaveWar(war);
                await channel.SendMessageAsync($"🔰**{usr}** claimed a base #{baseNumber} for a war against {war.ShortPrint()}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await channel.SendMessageAsync($"💢🔰 {ex.Message}").ConfigureAwait(false);
            }
        }

        [NadekoCommand, Usage, Description, Aliases]
        [RequireContext(ContextType.Guild)]
        public Task ClaimFinish1(int number, int baseNumber = 0) =>
            FinishClaim(Context, number, baseNumber - 1, 1);

        [NadekoCommand, Usage, Description, Aliases]
        [RequireContext(ContextType.Guild)]
        public Task ClaimFinish2(int number, int baseNumber = 0) =>
            FinishClaim(Context, number, baseNumber - 1, 2);

        [NadekoCommand, Usage, Description, Aliases]
        [RequireContext(ContextType.Guild)]
        public Task ClaimFinish(int number, int baseNumber = 0) =>
            FinishClaim(Context, number, baseNumber - 1);

        [NadekoCommand, Usage, Description, Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task EndWar(int number)
        {
            var channel = (SocketTextChannel)Context.Channel;

            var warsInfo = GetWarInfo((SocketGuild)Context.Guild,number);
            if (warsInfo == null)
            {
                await channel.SendMessageAsync("💢🔰 That war does not exist.").ConfigureAwait(false);
                return;
            }
            var war = warsInfo.Item1[warsInfo.Item2];
            war.End();
            SaveWar(war);
            await channel.SendMessageAsync($"❗🔰**War against {warsInfo.Item1[warsInfo.Item2].ShortPrint()} ended.**").ConfigureAwait(false);

            var size = warsInfo.Item1[warsInfo.Item2].Size;
            warsInfo.Item1.RemoveAt(warsInfo.Item2);
        }

        [NadekoCommand, Usage, Description, Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task Unclaim(int number, [Remainder] string otherName = null)
        {
            var channel = (SocketTextChannel)Context.Channel;

            var warsInfo = GetWarInfo((SocketGuild)Context.Guild, number);
            if (warsInfo == null || warsInfo.Item1.Count == 0)
            {
                await channel.SendMessageAsync("💢🔰 **That war does not exist.**").ConfigureAwait(false);
                return;
            }
            var usr =
                string.IsNullOrWhiteSpace(otherName) ?
                Context.User.Username :
                otherName;
            try
            {
                var war = warsInfo.Item1[warsInfo.Item2];
                var baseNumber = war.Uncall(usr);
                SaveWar(war);
                await channel.SendMessageAsync($"🔰 @{usr} has **UNCLAIMED** a base #{baseNumber + 1} from a war against {war.ShortPrint()}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await channel.SendMessageAsync($"💢🔰 {ex.Message}").ConfigureAwait(false);
            }
        }

        private async Task FinishClaim(CommandContext context, int number, int baseNumber, int stars = 3)
        {
            var channel = (SocketTextChannel)context.Channel;
            var warInfo = GetWarInfo(channel.Guild, number);
            if (warInfo == null || warInfo.Item1.Count == 0)
            {
                await channel.SendMessageAsync("💢🔰 **That war does not exist.**").ConfigureAwait(false);
                return;
            }
            var war = warInfo.Item1[warInfo.Item2];
            try
            {
                if (baseNumber == -1)
                {
                    baseNumber = war.FinishClaim(context.User.Username, stars);
                    SaveWar(war);
                }
                else
                {
                    war.FinishClaim(baseNumber, stars);
                }
                await channel.SendMessageAsync($"❗🔰{context.User.Mention} **DESTROYED** a base #{baseNumber + 1} in a war against {war.ShortPrint()}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await channel.SendMessageAsync($"💢🔰 {ex.Message}").ConfigureAwait(false);
            }
        }

        private static Tuple<List<ClashWar>, int> GetWarInfo(SocketGuild guild, int num)
        {
            //check if there are any wars
            List<ClashWar> wars = null;
            ClashWars.TryGetValue(guild.Id, out wars);
            if (wars == null || wars.Count == 0)
            {
                return null;
            }
            // get the number of the war
            else if (num < 1 || num > wars.Count)
            {
                return null;
            }
            num -= 1;
            //get the actual war
            return new Tuple<List<ClashWar>, int>(wars, num);
        }

        public static async Task<ClashWar> CreateWar(string enemyClan, int size, ulong serverId, ulong channelId)
        {
            using (var uow = DbHandler.UnitOfWork())
            {
                var cw = new ClashWar
                {
                    EnemyClan = enemyClan,
                    Size = size,
                    Bases = new List<ClashCaller>(size),
                    GuildId = serverId,
                    ChannelId = channelId,
                    Channel = NadekoBot.Client.GetGuild(serverId)
                                       ?.GetTextChannel(channelId)
                };
                cw.Bases.Capacity = size;
                for (int i = 0; i < size; i++)
                {
                    cw.Bases.Add(new ClashCaller()
                    {
                        CallUser = null,
                        SequenceNumber = i,
                    });
                }
                Console.WriteLine(cw.Bases.Capacity);
                uow.ClashOfClans.Add(cw);
                await uow.CompleteAsync();
                return cw;
            }
        }

        public static void SaveWar(ClashWar cw)
        {
            if (cw.WarState == ClashWar.StateOfWar.Ended)
            {
                using (var uow = DbHandler.UnitOfWork())
                {
                    uow.ClashOfClans.Remove(cw);
                    uow.CompleteAsync();
                }
                return;
            }


            using (var uow = DbHandler.UnitOfWork())
            {
                uow.ClashOfClans.Update(cw);
                uow.CompleteAsync();
            }
        }
    }
}
