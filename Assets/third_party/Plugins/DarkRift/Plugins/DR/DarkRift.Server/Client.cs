﻿/*
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Net;

using System.Threading;
using System.Net.Sockets;
using DarkRift.Dispatching;
using System.Diagnostics;
using DarkRift.DataStructures;
using DarkRift.Server.Metrics;
using DarkRift.Server.Plugins.Commands;

namespace DarkRift.Server
{
    /// <inheritDoc />
    internal sealed class Client : IClient, IDisposable
    {
        /// <inheritdoc/>
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <inheritdoc/>
        public event EventHandler<StrikeEventArgs> StrikeOccured;

        /// <inheritdoc/>
        public ushort ID { get; }

        /// <inheritdoc/>
        public IPEndPoint RemoteTcpEndPoint => connection.GetRemoteEndPoint("tcp");

        /// <inheritdoc/>
        public IPEndPoint RemoteUdpEndPoint => connection.GetRemoteEndPoint("udp");

        /// <inheritdoc/>
        public ConnectionState ConnectionState => connection.ConnectionState;
        /// <inheritdoc/>
        public DateTime ConnectionTime { get; }

        /// <inheritdoc/>
        public uint MessagesSent => (uint)Volatile.Read(ref messagesSent);

        private int messagesSent;

        /// <inheritdoc/>
        public uint MessagesPushed => (uint)Volatile.Read(ref messagesPushed);

        private int messagesPushed;

        /// <inheritdoc/>
        public uint MessagesReceived => (uint)Volatile.Read(ref messagesReceived);

        private int messagesReceived;

        /// <inheritdoc/>
        public IEnumerable<IPEndPoint> RemoteEndPoints => connection.RemoteEndPoints;

        /// <summary>
        ///     The connection to the client.
        /// </summary>
        private readonly NetworkServerConnection connection;

        /// <summary>
        ///     The client manager in charge of this client.
        /// </summary>
        private readonly ClientManager clientManager;

        /// <summary>
        ///     The thread helper this client will use.
        /// </summary>
        private readonly DarkRiftThreadHelper threadHelper;

        /// <summary>
        ///     The logger this client will use.
        /// </summary>
        private readonly Logger logger;

        /// <summary>
        ///     Counter metric of the number of messages sent.
        /// </summary>
        private readonly ICounterMetric messagesSentCounter;

        /// <summary>
        ///     Counter metric of the number of messages received.
        /// </summary>
        private readonly ICounterMetric messagesReceivedCounter;

        /// <summary>
        ///     Histogram metric of the time taken to execute the <see cref="MessageReceived"/> event.
        /// </summary>
        private readonly IHistogramMetric messageReceivedEventTimeHistogram;

        /// <summary>
        ///     Counter metric of failures executing the <see cref="MessageReceived"/> event.
        /// </summary>
        private readonly ICounterMetric messageReceivedEventFailuresCounter;

        /// <summary>
        ///     Creates a new client connection with a given global identifier and the client they are connected through.
        /// </summary>
        /// <param name="connection">The connection we handle.</param>
        /// <param name="id">The ID we've been assigned.</param>
        /// <param name="clientManager">The client manager in charge of this client.</param>
        /// <param name="threadHelper">The thread helper this client will use.</param>
        /// <param name="logger">The logger this client will use.</param>
        /// <param name="metricsCollector">The metrics collector this client will use.</param>
        internal static Client Create(NetworkServerConnection connection, ushort id, ClientManager clientManager, DarkRiftThreadHelper threadHelper, Logger logger, MetricsCollector metricsCollector)
        {
            Client client = new Client(connection, id, clientManager, threadHelper, logger, metricsCollector);

            return client;
        }

        /// <summary>
        ///     Creates a new client connection with a given global identifier and the client they are connected through.
        /// </summary>
        /// <param name="connection">The connection we handle.</param>
        /// <param name="id">The ID assigned to this client.</param>
        /// <param name="clientManager">The client manager in charge of this client.</param>
        /// <param name="threadHelper">The thread helper this client will use.</param>
        /// <param name="logger">The logger this client will use.</param>
        /// <param name="metricsCollector">The metrics collector this client will use.</param>
        private Client(NetworkServerConnection connection, ushort id, ClientManager clientManager, DarkRiftThreadHelper threadHelper, Logger logger, MetricsCollector metricsCollector)
        {
            this.connection = connection;
            this.ID = id;
            this.clientManager = clientManager;
            this.threadHelper = threadHelper;
            this.logger = logger;
            this.ConnectionTime = DateTime.UtcNow;

            connection.MessageReceived = HandleIncomingDataBuffer;
            connection.Disconnected = Disconnected;
            messagesSentCounter = metricsCollector.Counter("messages_sent", "The number of messages sent to clients.");
            messagesReceivedCounter = metricsCollector.Counter("messages_received", "The number of messages received from clients.");
            messageReceivedEventTimeHistogram = metricsCollector.Histogram("message_received_event_time", "The time taken to execute the MessageReceived event.");
            messageReceivedEventFailuresCounter = metricsCollector.Counter("message_received_event_failures", "The number of failures executing the MessageReceived event.");
        }
        public byte defaultMessageChannel = 0;
        /// <summary>
        ///     Sends the client their ID.
        /// </summary>
        private void SendID()
        {
            using (DarkRiftWriter writer = DarkRiftWriter.Create())
            {
                writer.Write(ID);

                using (Message command = Message.Create(BasisTags.Configure, writer))
                {
                    PushBuffer(command.ToBuffer(), defaultMessageChannel, DeliveryMethod.ReliableOrdered);

                    // Make sure we trigger the sent metric still
                    messagesSentCounter.Increment();
                }
            }
        }

        /// <summary>
        /// Starts this client's connecting listening for messages.
        /// </summary>
        internal void StartListening()
        {
            try
            {
                connection.StartListening();
                SendID();
            }
            catch (Exception e)
            {
                logger.Error("Failed to start listening to connection.", e);
                clientManager.HandleDisconnection(this, false, SocketError.SocketError, e);
            }
        }

        /// <inheritdoc/>
        public bool SendMessage(Message message,byte channel, DeliveryMethod sendMode)
        {
            //Send frame
            if (!PushBuffer(message.ToBuffer(), channel, sendMode))
                return false;

            //Increment counter
            Interlocked.Increment(ref messagesSent);
            messagesSentCounter.Increment();

            return true;
        }

        /// <inheritdoc/>
        public bool Disconnect()
        {
            if (!connection.Disconnect())
                return false;

            clientManager.HandleDisconnection(this, true, SocketError.Disconnecting, null);

            return true;
        }

        /// <summary>
        ///     Disconnects the connection without invoking events for plugins.
        /// </summary>
        internal bool DropConnection()
        {
            clientManager.DropClient(this);

            return connection.Disconnect();
        }

        /// <inheritdoc/>
        public IPEndPoint GetRemoteEndPoint(string name)
        {
            return connection.GetRemoteEndPoint(name);
        }

        /// <summary>
        ///     Handles a remote disconnection.
        /// </summary>
        /// <param name="error">The error that caused the disconnection.</param>
        /// <param name="exception">The exception that caused the disconnection.</param>
        private void Disconnected(SocketError error, Exception exception)
        {
            clientManager.HandleDisconnection(this, false, error, exception);
        }

        /// <summary>
        ///     Handles data that was sent from this client.
        /// </summary>
        /// <param name="buffer">The buffer that was received.</param>
        /// <param name="channel"></param>
        /// <param name="sendMode">The method data was sent using.</param>
        internal void HandleIncomingDataBuffer(MessageBuffer buffer,byte channel, DeliveryMethod sendMode)
        {
            //Add to received message counter
            Interlocked.Increment(ref messagesReceived);
            messagesReceivedCounter.Increment();

            Message message;
            try
            {
                message = Message.Create(buffer, true);
            }
            catch (IndexOutOfRangeException)
            {
                Strike(StrikeReason.InvalidMessageLength, "The message received was not long enough to contain the header.", 5);
                return;
            }

            try
            {
                HandleIncomingMessage(message, channel, sendMode);
            }
            finally
            {
                message.Dispose();
            }
        }

        /// <summary>
        ///     Handles messages that were sent from this client.
        /// </summary>
        /// <param name="message">The message that was received.</param>
        /// <param name="channel"></param>
        /// <param name="sendMode">The method data was sent using.</param>
        internal void HandleIncomingMessage(Message message,byte channel, DeliveryMethod sendMode)
        {
            //Discard any command messages sent from the client since they shouldn't send them
            if (message.Tag == BasisTags.Configure || message.Tag == BasisTags.Identify)
            {
                Strike(StrikeReason.InvalidCommand, "Received a command message from the client. Clients should not sent commands.", 5);

                return;
            }

            // Get another reference to the message so 1. we can control the backing array's lifecycle and thus it won't get disposed of before we dispatch, and
            // 2. because the current message will be disposed of when this method returns.
            Message messageReference = message.Clone();

            void DoMessageReceived()
            {
                MessageReceivedEventArgs args = MessageReceivedEventArgs.Create(messageReference,channel,sendMode,this);

                long startTimestamp = Stopwatch.GetTimestamp();
                try
                {
                    MessageReceived?.Invoke(this, args);
                }
                catch (Exception e)
                {
                    logger.Error("A plugin encountered an error whilst handling the MessageReceived event.", e);

                    messageReceivedEventFailuresCounter.Increment();
                    return;
                }
                finally
                {
                    // Now we've executed everything, dispose the message reference and release the backing array!
                    args.Dispose();
                    messageReference.Dispose();
                }

                double time = (double)(Stopwatch.GetTimestamp() - startTimestamp) / Stopwatch.Frequency;
                messageReceivedEventTimeHistogram.Report(time);
            }

            //Inform plugins
            threadHelper.DispatchIfNeeded(DoMessageReceived);
        }

        /// <summary>
        ///     Pushes a buffer to the client.
        /// </summary>
        /// <param name="buffer">The buffer to push.</param>
        /// <param name="channel"></param>
        /// <param name="sendMode">The method to send the data using.</param>
        /// <returns>Whether the send was successful.</returns>
        private bool PushBuffer(MessageBuffer buffer,byte channel, DeliveryMethod sendMode)
        {
            if (!connection.SendMessage(buffer, channel, sendMode))
                return false;

            Interlocked.Increment(ref messagesPushed);

            return true;
        }
        
#region Strikes

        /// <inheritdoc/>
        public void Strike(string message = null)
        {
            Strike(StrikeReason.PluginRequest, message, 1);
        }

        /// <inheritdoc/>
        public void Strike(string message = null, int weight = 1)
        {
            Strike(StrikeReason.PluginRequest, message, weight);
        }

        /// <summary>
        ///     Informs plugins and adds a strike to this client's record.
        /// </summary>
        /// <param name="reason">The reason for the strike.</param>
        /// <param name="message">A message describing the reason for the strike.</param>
        /// <param name="weight">The number of strikes this accounts for.</param>
        internal void Strike(StrikeReason reason, string message, int weight)
        {
            EventHandler<StrikeEventArgs> handler = StrikeOccured;
            if (handler != null)
            {
                StrikeEventArgs args = new StrikeEventArgs(reason, message, weight);

                void DoInvoke()
                {
                    try
                    {
                        handler.Invoke(this, args);
                    }
                    catch (Exception e)
                    {
                        logger.Error("A plugin encountered an error whilst handling the StrikeOccured event. The strike will stand. (See logs for exception)", e);
                    }
                }

                void AfterInvoke(ActionDispatcherTask t)
                {
                    if (t == null || t.Exception == null)
                    {
                        if (args.Forgiven)
                            return;
                    }

                    EnforceStrike(reason, message, args.Weight);
                }

                threadHelper.DispatchIfNeeded(DoInvoke, AfterInvoke);
            }
            else
            {
                EnforceStrike(reason, message, weight);
            }
        }

        /// <summary>
        ///     Adds a strike to this client's record.
        /// </summary>
        /// <param name="reason">The reason for the strike.</param>
        /// <param name="message">A message describing the reason for the strike.</param>
        /// <param name="weight">The number of strikes this accounts for.</param>
        private void EnforceStrike(StrikeReason reason, string message, int weight)
        {
            logger.Trace($"Client received strike of weight {weight} for {reason}{(message == null ? "" : ": " + message)}.");
            Disconnect();
            logger.Info($"Client was disconnected as the total weight of accumulated strikes exceeded the allowed number");
        }
#endregion

        /// <summary>
        ///     Disposes of this client.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

#pragma warning disable CS0628
        protected void Dispose(bool disposing)
        {
            if (disposing)
                connection.Dispose();
        }
#pragma warning restore CS0628
    }
}