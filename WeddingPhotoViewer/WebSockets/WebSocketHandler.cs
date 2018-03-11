﻿using Microsoft.AspNetCore.Http;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;

namespace WeddingPhotoViewer
{
    public class WebSocketHandler
    {
        private ILogger logger;
        private const int pingInterval = 5;
        private const string PING_MESSAGE = "hey";
        private const string PONG_MESSAGE = "hoi";

        private ConcurrentDictionary<string, WebSocket> browsers = new ConcurrentDictionary<string, WebSocket>();
        private ConcurrentQueue<WebSocketMessage> messageQueue = new ConcurrentQueue<WebSocketMessage>();

        public WebSocketHandler(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger<WebSocketHandler>();
        }

        private CancellationToken CreateDefaultCancelToken()
        {
            return new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token;
        }
        private void SetPing(WebSocket webSocket, CancellationTokenSource cancelToken)
        {
            IObservable<long> observable = Observable.Interval(TimeSpan.FromSeconds(pingInterval));
            observable.Subscribe(
                async i =>
                {
                    if (webSocket.State == WebSocketState.Open)
                    {
                        await webSocket.SendAsync(System.Text.Encoding.UTF8.GetBytes(PING_MESSAGE), WebSocketMessageType.Text, true, CreateDefaultCancelToken());
                        logger.LogInformation("ping send.{0}", i);
                    }
                },
                x => logger.LogInformation("ping completed."), cancelToken.Token);
        }

        public async Task Photo(HttpContext context, WebSocket webSocket)
        {
            var id = context.TraceIdentifier;
            if (browsers.TryAdd(id, webSocket))
            {
                logger.LogInformation("clinet {0} is connected.", id);
            }

            var cancelTokenPing = new CancellationTokenSource();

            try
            {
                SetPing(webSocket, cancelTokenPing);

                // send queued message
                foreach (var msg in messageQueue)
                {
                    await webSocket.SendAsync(msg.Message, msg.MessageType, msg.EndOfMessage, CreateDefaultCancelToken());
                }

                var buffer = new byte[1024 * 4];
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CreateDefaultCancelToken());
                while (!result.CloseStatus.HasValue)
                {
                    // browser keep alive message
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CreateDefaultCancelToken());
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var msg = System.Text.Encoding.UTF8.GetString(buffer.Take(result.Count).ToArray());
                        if (msg == PING_MESSAGE)
                        {
                            logger.LogInformation("ping received.:\"{0}\"\n", msg);
                        }
                        else if (msg == PONG_MESSAGE)
                        {
                            logger.LogInformation("pong received.:\"{0}\"\n", msg);
                        } else
                        {
                            logger.LogInformation("unknown message received.\"{0}\"\n", msg);
                        }
                    } else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        logger.LogError("socket closed by client.");
                        break;
                    }
                }
                await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
            }
            catch (OperationCanceledException oex)
            {
                logger.LogError("{0} is disconnected.{1}", id, oex);
            }
            catch (Exception ex)
            {
                logger.LogError("{0} is disconnected.{1}", id, ex);
            }

            // stop ping
            cancelTokenPing.Cancel();

            if (browsers.TryRemove(id, out WebSocket socket)) {
                logger.LogInformation("clinet {0} is removed.", id);
            }
        }

        public async Task Webjob(HttpContext context, WebSocket webSocket)
        {
            var buffer = new byte[1024 * 4];
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            while (!result.CloseStatus.HasValue)
            {
                // 内部キューに保存
                var sendMessage = new ArraySegment<byte>(buffer, 0, result.Count);

                var oneMsg = new WebSocketMessage()
                {
                    EndOfMessage = result.EndOfMessage,
                    MessageType = result.MessageType,
                };
                var queueBuffer = new byte[1024 * 4];
                sendMessage.CopyTo(queueBuffer);
                oneMsg.Message = new ArraySegment<byte>(queueBuffer, 0, result.Count);
                messageQueue.Enqueue(oneMsg);

                if (messageQueue.Count() > 10) {
                    if (messageQueue.TryDequeue(out WebSocketMessage msg))
                    {
                        Console.WriteLine(msg.ToString());
                    }
                }

                List<string> deleteList = new List<string>();
                foreach (var browser in browsers)
                {
                    try
                    {
                        if (browser.Value.State == WebSocketState.Open)
                        {
                            // send image url
                            await browser.Value.SendAsync(sendMessage, result.MessageType, result.EndOfMessage, CancellationToken.None);
                        } else
                        {
                            deleteList.Add(browser.Key);
                        }
                    }
                    catch (Exception ex)
                    {
                        // ignore
                        deleteList.Add(browser.Key);
                    }
                }
                if (deleteList.Any())
                {
                    deleteList.ForEach(x =>
                   {
                       if (browsers.TryRemove(x, out WebSocket socket))
                       {
                           logger.LogInformation("clinet {0} is removed.", x);
                       }
                   });
                }
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            }
            await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
        }
    }

    public class WebSocketMessage {
        public ArraySegment<byte> Message { get; set; }
        public bool EndOfMessage { get; set; }
        public WebSocketMessageType MessageType { get; set; }
    }
}
