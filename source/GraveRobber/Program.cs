﻿/*
 * GraveRobber. A .NET PoC program for fetching data from the SOCVR graveyards.
 * Copyright © 2015, ArcticEcho.
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 2
 * of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, see <http://www.gnu.org/licenses/>.
 */





using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ChatExchangeDotNet;

namespace GraveRobber
{
    class Program
    {
        private static readonly string currentVer = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion;
        private static readonly ManualResetEvent shutdownMre = new ManualResetEvent(false);
        private static readonly MessageFetcher messageFetcher = new MessageFetcher();
        private static readonly SELogin seLogin = new SELogin();
        private static QuestionProcessor qProcessor;
        private static Client chatClient;
        private static Room mainRoom;
        private static Room watchingRoom;



        public static void Main(string[] args)
        {
            Console.Title = $"GraveRobber {currentVer}";
            Console.CancelKeyPress += (o, oo) =>
            {
                oo.Cancel = true;
                shutdownMre.Set();
            };

            Console.Write("Authenticating...");
            InitialiseFromConfig();
            Console.Write("done.\nStarting question processor...");
            StartQuestionProcessor();
            Console.Write("done.\nJoining chat room(s)...");
            JoinRooms();

#if DEBUG
            Console.WriteLine("done.\nGraveRobber started (debug).");
            mainRoom.PostMessageFast($"GraveRobber started {currentVer} (debug).");
#else
            mainRoom.PostMessageFast($"GraveRobber started {currentVer}.");
            Console.WriteLine("done.\nGraveRobber started.");
#endif

            //var ms = messageFetcher.GetRecentMessage(mainRoom, 500);

            //foreach (var m in ms.Values)
            //{
            //    if (!string.IsNullOrWhiteSpace(m))
            //    {
            //        qProcessor.WatchPost(m);
            //    }
            //}

            shutdownMre.WaitOne();

            Console.Write("Stopping...");

            mainRoom?.Leave();
            shutdownMre?.Dispose();
            chatClient?.Dispose();
            qProcessor?.Dispose();

            Console.WriteLine("done.");
        }



        private static void InitialiseFromConfig()
        {
            var cr = new ConfigReader();

            chatClient = new Client(cr.AccountEmailAddressPrimary, cr.AccountPasswordPrimary);

            seLogin.SEOpenIDLogin(cr.AccountEmailAddressSecondary, cr.AccountPasswordSecondary);
            seLogin.SiteLogin("stackoverflow.com");
        }

        private static void StartQuestionProcessor()
        {
            qProcessor = new QuestionProcessor(seLogin);
        }

        private static void JoinRooms()
        {
            var cr = new ConfigReader();

            mainRoom = chatClient.JoinRoom(cr.RoomUrl);
            mainRoom.InitialisePrimaryContentOnly = true;
            mainRoom.EventManager.ConnectListener(EventType.UserMentioned, new Action<Message>(HandleCommand));

            watchingRoom = chatClient.JoinRoom("http://chat.stackoverflow.com/rooms/90230/cv-request-graveyard");//("http://chat.stackoverflow.com/rooms/68414/socvr-testing-facility");//
            watchingRoom.InitialisePrimaryContentOnly = true;
            watchingRoom.EventManager.ConnectListener(EventType.MessageMovedIn, new Action<Message>(m =>
            {
                var url = messageFetcher.GetPostUrl(m);

                if (!String.IsNullOrWhiteSpace(url))
                {
                    qProcessor.WatchPost(url);
                }
            }));
        }

        /// <summary>
        /// Beware, laziness ahead...
        /// </summary>
        private static void HandleCommand(Message msg)
        {
            var cmd = msg.Content.Trim().ToUpperInvariant();

            if (cmd == "DIE" && (msg.Author.IsRoomOwner ||
                msg.Author.IsMod || msg.Author.ID == 2246344))
            {
                mainRoom.PostMessageFast("Bye.");
                shutdownMre.Set();
            }
            else if (cmd == "REFRESH" && (msg.Author.IsRoomOwner ||
                     msg.Author.Reputation > 3000))
            {
                mainRoom.PostMessageFast("Forcing refresh, one moment...");
                Task.Run(() =>
                {
                    try
                    {
                        qProcessor.Refresh();
                        mainRoom.PostMessageFast("Refresh complete.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                });
            }
            else if (cmd.StartsWith("CHECK GRAVE") && (msg.Author.IsRoomOwner ||
                     msg.Author.Reputation > 3000))
            {
                var postCount = 10;

                if (cmd.Any(Char.IsDigit))
                {
                    if (!int.TryParse(new string(cmd.Where(Char.IsDigit).ToArray()), out postCount))
                    {
                        // Well, do nothing since we've already initialised
                        // the field with a default value (of 10).
                    }
                }

                CheckGrave(postCount);
            }
            else if (cmd == "COMMANDS")
            {
                mainRoom.PostMessageFast("    commands ~~~~~~~~~~~~~ Prints this beautifully formatted message.\n" +
                                         "    stats ~~~~~~~~~~~~~~~~ Prints the number of posts being watched, and closed and edited.\n" +
                                         "    refresh ~~~~~~~~~~~~~~ Forces a refresh of the \"closed edited posts\" list.\n" +
                                         "    check grave <number> ~ Posts a list of edited closed posts (default of ten, unless specified).\n" +
                                         "    die ~~~~~~~~~~~~~~~~~~ I die a slow and painful death.");
            }
            else if (cmd == "STATS")
            {
                var pendingQs = qProcessor.PostsPendingReview.Count;
                var watchingQs = qProcessor.WatchedPosts;
                mainRoom.PostMessageFast($"Posts being watched: `{watchingQs}`. Posts pending review: `{pendingQs}`.");
            }
            else if (cmd == "DIE")
            {
                mainRoom.PostReplyFast(msg, "You need to be a room owner, moderator, or Sam to kill me.");
            }
            else if (cmd == "REFRESH" || cmd.StartsWith("CHECK GRAVE"))
            {
                mainRoom.PostReplyFast(msg, "You need 3000 reputation to run this command.");
            }
        }

        private static void CheckGrave(int postCount)
        {
            mainRoom.PostMessageFast("Digging up graves, one moment...");

            try
            {
                var chatMsg = new MessageBuilder();
                var posts = new HashSet<QuestionStatus>();

                foreach (var post in qProcessor.PostsPendingReview.OrderByDescending(x => x.Difference))
                {
                    if (posts.Count > postCount) break;

                    posts.Add(post);
                    chatMsg.AppendText($"{Math.Round(post.Difference * 100)}% changed, ");
                    chatMsg.AppendText($"score +{post.UpvoteCount}/-{Math.Abs(post.DownvoteCount)}: {post.Url}\n");
                }

                if (!string.IsNullOrWhiteSpace(chatMsg.ToString()))
                {
                    mainRoom.PostMessageFast(chatMsg);

                    foreach (var post in posts)
                    {
                        qProcessor.PostsPendingReview.RemoveItem(post);
                    }
                }
                else
                {
                    mainRoom.PostMessageFast("No questions found.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}
