using Discord;
using Discord.WebSocket;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Data.SQLite;


namespace ChannelMap
{
    class Program
    {
        DiscordSocketClient Client;
        SQLiteConnection dbConnection;

        static void Main(string[] args)
        {
            string token = "";
            if(!(args.Length == 1)) {
                Console.WriteLine("Usage: dotnet ChannelMap [Token]");
                Console.WriteLine("Enter Bot Token");
                token = Console.ReadLine();
            }
            else {
                token = args[0];
            }

            new Program().MainAsync(token).GetAwaiter().GetResult();
        }

        public async Task MainAsync(string token)
        {

            dbConnection = new SQLiteConnection("Data Source=mappings.db;Version=3;");

            if(!File.Exists("mappings.db")) {
                SQLiteConnection.CreateFile("mappings.db");


                string sql = @"
            CREATE TABLE `Mappings` (
                `VoiceID`	TEXT,
                `TextID`	TEXT,
                `Guild`	TEXT,
                PRIMARY KEY(`VoiceID`)
            );";

                SQLiteCommand command = new SQLiteCommand(sql , dbConnection);
                dbConnection.Open();
                command.ExecuteNonQuery();
                dbConnection.Close();

            }

            Client = new DiscordSocketClient(
             new DiscordSocketConfig() {
                 LogLevel = LogSeverity.Info
             });

            try {
                await Client.LoginAsync(TokenType.Bot , token);
                await Client.StartAsync();
            }
            catch(Exception ex) {
                Console.WriteLine(ex.Message + "\nPress any key to exit");
                Console.ReadKey();
                Environment.Exit(1);
            };

            Client.UserVoiceStateUpdated += VoiceStateUpdated;
            Client.JoinedGuild += JoinGuild;
            Client.Log += (LogMessage message) =>
            {
                Console.WriteLine($"{DateTime.Now.ToString("[MM/dd/yyyy HH:mm]")} {message.Source}: {message.Message}");
                return Task.CompletedTask;
            };
            await Task.Delay(-1);
        }

        //Creates text channels for all voice channels upon joining a new server
        private async Task JoinGuild(SocketGuild guild)
        {
            Log($"Setting up Links for {guild.Name} - {guild.Id}");
            try {
                foreach(SocketVoiceChannel voiceChannel in guild.VoiceChannels)
                    await InsertChannel(voiceChannel);

            }
            catch(Exception ex) {
                Console.WriteLine(ex.Message);
            }
        }


        //creates a new channel and inserts the values into a NOSQL database
        private async Task InsertChannel(SocketVoiceChannel voiceChannel)
        {

            ITextChannel textChannel = await voiceChannel.Guild.CreateTextChannelAsync($"{voiceChannel.Name} - Voice");
            await textChannel.ModifyAsync(channel => channel.Topic = $"  {voiceChannel.Name} ->  {textChannel.Name}");

            Log($"Created new Text Channel in {textChannel.GuildId}: {voiceChannel.Id} -> {textChannel.Id}");

            string insert = @"INSERT INTO Mappings (VoiceID, TextID, Guild) VALUES ($VoiceID, $TextID, $Guild);";
            SQLiteCommand command = new SQLiteCommand(insert , dbConnection);
            command.Parameters.Add(new SQLiteParameter("$VoiceID"));
            command.Parameters.Add(new SQLiteParameter("$TextID"));
            command.Parameters.Add(new SQLiteParameter("$Guild"));
            command.Parameters["$VoiceID"].Value = voiceChannel.Id;
            command.Parameters["$TextID"].Value = textChannel.Id;
            command.Parameters["$Guild"].Value = voiceChannel.Guild.Id;

            dbConnection.Open();
            command.ExecuteScalar();
            dbConnection.Close();

            OverwritePermissions overwrite = new OverwritePermissions(readMessages: PermValue.Deny);
            await textChannel.AddPermissionOverwriteAsync(voiceChannel.Guild.EveryoneRole , overwrite);

        }

        //gets the TextChannelID from the VoiceChannel
        private async Task<ulong> GetChannelLink(SocketVoiceChannel voiceChannel)
        {
            ulong value;
            string select = "SELECT textID FROM Mappings WHERE VoiceID = $VoiceID";
            SQLiteCommand command = new SQLiteCommand(select , dbConnection);
            command.Parameters.Add(new SQLiteParameter("$VoiceID"));
            command.Parameters["$VoiceID"].Value = voiceChannel.Id;
            dbConnection.Open();
            var result = command.ExecuteScalar();
            dbConnection.Close();

            if(result == null) {
                InsertChannel(voiceChannel).GetAwaiter().GetResult();
                value = await GetChannelLink(voiceChannel);

            }
            else {
                value = ulong.Parse((string)result);
            }
            return value;
        }

        private async Task VoiceStateUpdated(SocketUser user , SocketVoiceState before , SocketVoiceState after)
        {
            //user leaves voice
            if(after.VoiceChannel == null) {
                await HandleDisconnect(user , (ISocketMessageChannel)Client.GetChannel(await GetChannelLink(before.VoiceChannel)));
            }
            //user joins voice
            else if(before.VoiceChannel == null) {
                await HandleConnect(user , (ISocketMessageChannel)Client.GetChannel(await GetChannelLink(after.VoiceChannel)));
            }
            //user swiches voice channels
            else {
                await HandleDisconnect(user , (ISocketMessageChannel)Client.GetChannel(await GetChannelLink(before.VoiceChannel)));
                await HandleConnect(user , (ISocketMessageChannel)Client.GetChannel(await GetChannelLink(after.VoiceChannel)));
            }

        }

        //updates channel permissions when a user joins a voice channel
        public Task HandleConnect(SocketUser socketUser , ISocketMessageChannel targetChannel)
        {
            OverwritePermissions overwrite = new OverwritePermissions(readMessages: PermValue.Allow);
            IGuildChannel textChannel = (IGuildChannel)targetChannel;

            textChannel.AddPermissionOverwriteAsync(socketUser , overwrite);
            EmbedBuilder builder = new EmbedBuilder() {
                Description = ":speaker: " + socketUser.Mention + " has joined the voice channel" ,
                Color = Color.Green ,
                ThumbnailUrl = socketUser.GetAvatarUrl(ImageFormat.Auto , 128)
            };
            targetChannel.SendMessageAsync("" , false , builder.Build());
            return Task.CompletedTask;
        }

        //updates channel permissions when a user leaves a voice channel
        public Task HandleDisconnect(SocketUser socketUser , ISocketMessageChannel targetChannel)
        {
            // Don't hide the channel for users that can manage messages
            if (!((SocketGuildUser)socketUser).GuildPermissions.ManageMessages) {
                OverwritePermissions overwrite = new OverwritePermissions(readMessages: PermValue.Deny);
                IGuildChannel textChannel = (IGuildChannel)targetChannel;
                textChannel.AddPermissionOverwriteAsync(socketUser , overwrite);
            }

            EmbedBuilder builder = new EmbedBuilder() {
                Description = ":mute: " + socketUser.Mention + " has left the voice channel  " ,
                Color = Color.Orange ,
                ThumbnailUrl = socketUser.GetAvatarUrl(ImageFormat.Auto , 128)
            };

            targetChannel.SendMessageAsync("" , false , builder.Build());
            return Task.CompletedTask;
        }


        private void Log(string message) =>
        Console.WriteLine($"{DateTime.Now.ToString("[MM/dd/yyyy HH:mm]")} {message}");

    }
}
