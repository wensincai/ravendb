﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Raven.Abstractions.Data;
using Raven.Abstractions.Replication;
using Raven.Client.Replication.Messages;
using Raven.Server.Documents.TcpHandlers;
using Raven.Server.Json;
using Raven.Server.ServerWide.Context;
using Sparrow.Collections;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Logging;

namespace Raven.Server.Documents.Replication
{
    public class DocumentReplicationLoader : IDisposable
    {
        private readonly DocumentDatabase _database;
        private volatile bool _isInitialized;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly Timer _reconnectAttemptTimer;
        private readonly ConcurrentSet<OutgoingReplicationHandler> _outgoing = new ConcurrentSet<OutgoingReplicationHandler>();
        private readonly ConcurrentDictionary<ReplicationDestination, ConnectionFailureInfo> _outgoingFailureInfo = new ConcurrentDictionary<ReplicationDestination, ConnectionFailureInfo>();

        private readonly ConcurrentSet<IncomingReplicationHandler> _incoming = new ConcurrentSet<IncomingReplicationHandler>();
        private readonly ConcurrentDictionary<IncomingConnectionInfo, DateTime> _incomingLastActivityTime = new ConcurrentDictionary<IncomingConnectionInfo, DateTime>();
        private readonly ConcurrentDictionary<IncomingConnectionInfo, ConcurrentQueue<IncomingConnectionRejectionInfo>> _incomingRejectionStats = new ConcurrentDictionary<IncomingConnectionInfo, ConcurrentQueue<IncomingConnectionRejectionInfo>>();

        private readonly ConcurrentSet<ConnectionFailureInfo> _reconnectQueue = new ConcurrentSet<ConnectionFailureInfo>();

        private readonly Logger _log;
        private ReplicationDocument _replicationDocument;

        public IEnumerable<IncomingConnectionInfo> IncomingConnections => _incoming.Select(x => x.ConnectionInfo);
        public IEnumerable<ReplicationDestination> OutgoingConnections => _outgoing.Select(x => x.Destination);

        public DocumentReplicationLoader(DocumentDatabase database)
        {
            _database = database;
            _log = LoggingSource.Instance.GetLogger<DocumentReplicationLoader>(_database.Name);
            _reconnectAttemptTimer = new Timer(AttemptReconnectFailedOutgoing,
                null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        public IReadOnlyDictionary<ReplicationDestination, ConnectionFailureInfo> OutgoingFailureInfo => _outgoingFailureInfo;
        public IReadOnlyDictionary<IncomingConnectionInfo, DateTime> IncomingLastActivityTime => _incomingLastActivityTime;
        public IReadOnlyDictionary<IncomingConnectionInfo, ConcurrentQueue<IncomingConnectionRejectionInfo>> IncomingRejectionStats => _incomingRejectionStats;
        public IEnumerable<ReplicationDestination> ReconnectQueue => _reconnectQueue.Select(x => x.Destination);

        public void AcceptIncomingConnection(TcpConnectionOptions tcpConnectionOptions)
        {
            ReplicationLatestEtagRequest getLatestEtagMessage;
            using (var readerObject = tcpConnectionOptions.MultiDocumentParser.ParseToMemory("IncomingReplication/get-last-etag-message read"))
            {
                getLatestEtagMessage = JsonDeserializationServer.ReplicationLatestEtagRequest(readerObject);
                if (_log.IsInfoEnabled)
                {
                    _log.Info($"GetLastEtag: {getLatestEtagMessage.SourceMachineName} / {getLatestEtagMessage.SourceDatabaseName} ({getLatestEtagMessage.SourceDatabaseId}) - {getLatestEtagMessage.SourceUrl}");
                }
            }

            var connectionInfo = IncomingConnectionInfo.FromGetLatestEtag(getLatestEtagMessage);
            try
            {
                AssertValidConnection(connectionInfo);
            }
            catch (Exception e)
            {
                if (_log.IsInfoEnabled)
                    _log.Info($"Connection from [{connectionInfo}] is rejected.", e);

                var incomingConnectionRejectionInfos = _incomingRejectionStats.GetOrAdd(connectionInfo,
                    _ => new ConcurrentQueue<IncomingConnectionRejectionInfo>());
                incomingConnectionRejectionInfos.Enqueue(new IncomingConnectionRejectionInfo { Reason = e.ToString() });

                throw;
            }

            DocumentsOperationContext documentsOperationContext;
            TransactionOperationContext configurationContext;
            using (_database.DocumentsStorage.ContextPool.AllocateOperationContext(out documentsOperationContext))
            using (_database.ConfigurationStorage.ContextPool.AllocateOperationContext(out configurationContext))
            using (var writer = new BlittableJsonTextWriter(documentsOperationContext, tcpConnectionOptions.Stream))
            using (var docTx = documentsOperationContext.OpenReadTransaction())
            using (var configTx = configurationContext.OpenReadTransaction())
            {
                var documentsChangeVector = new DynamicJsonArray();
                foreach (var changeVectorEntry in _database.DocumentsStorage.GetDatabaseChangeVector(documentsOperationContext))
                {
                    documentsChangeVector.Add(new DynamicJsonValue
                    {
                        [nameof(ChangeVectorEntry.DbId)] = changeVectorEntry.DbId.ToString(),
                        [nameof(ChangeVectorEntry.Etag)] = changeVectorEntry.Etag
                    });
                }

                var indexesChangeVector = new DynamicJsonArray();
                var changeVectorAsArray = _database.IndexMetadataPersistence.GetIndexesAndTransformersChangeVector(configTx.InnerTransaction);
                foreach (var changeVectorEntry in changeVectorAsArray)
                {
                    indexesChangeVector.Add(new DynamicJsonValue
                    {
                        [nameof(ChangeVectorEntry.DbId)] = changeVectorEntry.DbId.ToString(),
                        [nameof(ChangeVectorEntry.Etag)] = changeVectorEntry.Etag
                    });
                }

                var lastEtagFromSrc = _database.DocumentsStorage.GetLastReplicateEtagFrom(documentsOperationContext, getLatestEtagMessage.SourceDatabaseId);
                if (_log.IsInfoEnabled)
                {
                    _log.Info($"GetLastEtag response, last etag: {lastEtagFromSrc}");
                }
                documentsOperationContext.Write(writer, new DynamicJsonValue
                {
                    [nameof(ReplicationMessageReply.Type)] = "Ok",
                    [nameof(ReplicationMessageReply.MessageType)] = ReplicationMessageType.Heartbeat,
                    [nameof(ReplicationMessageReply.LastEtagAccepted)] = lastEtagFromSrc,
                    [nameof(ReplicationMessageReply.LastIndexTransformerEtagAccepted)] = _database.IndexMetadataPersistence.GetLastReplicateEtagFrom(configTx.InnerTransaction, getLatestEtagMessage.SourceDatabaseId),
                    [nameof(ReplicationMessageReply.DocumentsChangeVector)] = documentsChangeVector,
                    [nameof(ReplicationMessageReply.IndexTransformerChangeVector)] = indexesChangeVector
                });
                writer.Flush();
            }

            var newIncoming = new IncomingReplicationHandler(
                   tcpConnectionOptions.MultiDocumentParser,
                   _database,
                   tcpConnectionOptions.TcpClient,
                   tcpConnectionOptions.Stream,
                   getLatestEtagMessage,
                   this);

            newIncoming.Failed += OnIncomingReceiveFailed;
            newIncoming.DocumentsReceived += OnIncomingReceiveSucceeded;

            if (_log.IsInfoEnabled)
                _log.Info($"Initialized document replication connection from {connectionInfo.SourceDatabaseName} located at {connectionInfo.SourceUrl}", null);

            _incoming.Add(newIncoming);

            newIncoming.Start();
        }

        private void AttemptReconnectFailedOutgoing(object state)
        {
            var minDiff = TimeSpan.FromSeconds(30);
            foreach (var failure in _reconnectQueue)
            {
                var diff = failure.RetryOn - DateTime.UtcNow;
                if (diff < TimeSpan.Zero)
                {
                    try
                    {
                        _reconnectQueue.TryRemove(failure);
                        AddAndStartOutgoingReplication(failure.Destination);
                    }
                    catch (Exception e)
                    {
                        if (_log.IsInfoEnabled)
                        {
                            _log.Info($"Failed to start outgoing replciation to {failure.Destination}", e);
                        }
                    }
                }
                else
                {
                    if (minDiff > diff)
                        minDiff = diff;
                }
            }

            try
            {
                //at this stage we can be already disposed, so ...
                _reconnectAttemptTimer.Change(minDiff, TimeSpan.FromDays(1));
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void AssertValidConnection(IncomingConnectionInfo connectionInfo)
        {

            if (Guid.Parse(connectionInfo.SourceDatabaseId) == _database.DbId)
            {
                throw new InvalidOperationException(
                    "Cannot have have replication with source and destination being the same database. They share the same db id ({connectionInfo} - {_database.DbId})");
            }

            foreach (var relevantActivityEntry in _incomingLastActivityTime)
            {
                if (relevantActivityEntry.Key.SourceDatabaseId.Equals(connectionInfo.SourceDatabaseId,
                        StringComparison.OrdinalIgnoreCase) == false)
                    continue;

                if (relevantActivityEntry.Key != null &&
                   (relevantActivityEntry.Value - DateTime.UtcNow).TotalMilliseconds <=
                   _database.Configuration.Replication.ActiveConnectionTimeout.AsTimeSpan.TotalMilliseconds)
                {
                    throw new InvalidOperationException(
                        $"Tried to connect [{connectionInfo}], but the connection from the same source was active less then {_database.Configuration.Replication.ActiveConnectionTimeout.AsTimeSpan.TotalSeconds} ago. Duplicate connections from the same source are not allowed.");
                }
            }
        }

        public void Initialize()
        {
            if (_isInitialized) //precaution -> probably not necessary, but still...
                return;

            _isInitialized = true;

            _database.Notifications.OnSystemDocumentChange += OnSystemDocumentChange;

            InitializeOutgoingReplications();
        }

        private void InitializeOutgoingReplications()
        {
            _replicationDocument = GetReplicationDocument();
            if (_replicationDocument?.Destinations == null || //precaution
                _replicationDocument.Destinations.Count == 0)
            {
                if (_log.IsInfoEnabled)
                    _log.Info("Tried to initialize outgoing replications, but there is no replication document or destinations are empty. Nothing to do...");
                return;
            }

            if (_log.IsInfoEnabled)
                _log.Info($"Initializing {_replicationDocument.Destinations.Count:#,#} outgoing replications..");

            foreach (var destination in _replicationDocument.Destinations)
            {
                AddAndStartOutgoingReplication(destination);
                if (_log.IsInfoEnabled)
                    _log.Info($"Initialized outgoing replication for [{destination.Database}/{destination.Url}]");
            }
            if (_log.IsInfoEnabled)
                _log.Info("Finished initialization of outgoing replications..");
        }

        private void AddAndStartOutgoingReplication(ReplicationDestination destination)
        {
            var outgoingReplication = new OutgoingReplicationHandler(_database, destination);
            outgoingReplication.Failed += OnOutgoingSendingFailed;
            outgoingReplication.SuccessfulTwoWaysCommunication += OnOutgoingSendingSucceeded;
            _outgoing.TryAdd(outgoingReplication); // can't fail, this is a brand new instace
            _outgoingFailureInfo.TryAdd(destination, new ConnectionFailureInfo
            {
                Destination = destination
            });
            outgoingReplication.Start();
        }

        private void OnIncomingReceiveFailed(IncomingReplicationHandler instance, Exception e)
        {
            using (instance)
            {
                _incoming.TryRemove(instance);

                instance.Failed -= OnIncomingReceiveFailed;
                instance.DocumentsReceived -= OnIncomingReceiveSucceeded;
                if (_log.IsInfoEnabled)
                    _log.Info($"Incoming replication handler has thrown an unhandled exception. ({instance.FromToString})", e);
            }
        }

        private void OnOutgoingSendingFailed(OutgoingReplicationHandler instance, Exception e)
        {
            using (instance)
            {
                _outgoing.TryRemove(instance);

                ConnectionFailureInfo failureInfo;
                if (_outgoingFailureInfo.TryGetValue(instance.Destination, out failureInfo) == false)
                    return;

                _reconnectQueue.Add(failureInfo);

                if (_log.IsInfoEnabled)
                    _log.Info($"Document replication connection ({instance.Destination}) failed, and the connection will be retried later.",
                        e);
            }
        }

        private void OnOutgoingSendingSucceeded(OutgoingReplicationHandler instance)
        {
            ConnectionFailureInfo failureInfo;
            if (_outgoingFailureInfo.TryGetValue(instance.Destination, out failureInfo))
                failureInfo.Reset();
        }

        private void OnIncomingReceiveSucceeded(IncomingReplicationHandler instance)
        {
            _incomingLastActivityTime.AddOrUpdate(instance.ConnectionInfo, DateTime.UtcNow, (_, __) => DateTime.UtcNow);
        }

        private void OnSystemDocumentChange(DocumentChangeNotification notification)
        {
            if (!notification.Key.Equals(Constants.Replication.DocumentReplicationConfiguration, StringComparison.OrdinalIgnoreCase))
                return;

            if (_log.IsInfoEnabled)
                _log.Info("System document change detected. Starting and stopping outgoing replication threads.");


            foreach (var instance in _outgoing)
                instance.Dispose();

            _outgoing.Clear();

            _outgoingFailureInfo.Clear();

            InitializeOutgoingReplications();

            if (_log.IsInfoEnabled)
                _log.Info($"Replication configuration was changed: {notification.Key}");
        }

        internal ReplicationDocument GetReplicationDocument()
        {
            DocumentsOperationContext context;
            using (_database.DocumentsStorage.ContextPool.AllocateOperationContext(out context))
            using (context.OpenReadTransaction())
            {
                var configurationDocument = _database.DocumentsStorage.Get(context, Constants.Replication.DocumentReplicationConfiguration);

                if (configurationDocument == null)
                    return null;

                using (configurationDocument.Data)
                {
                    return JsonDeserializationServer.ReplicationDocument(configurationDocument.Data);
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _reconnectAttemptTimer.Dispose();

            _database.Notifications.OnSystemDocumentChange -= OnSystemDocumentChange;

            if (_log.IsInfoEnabled)
                _log.Info("Closing and disposing document replication connections.");

            foreach (var incoming in _incoming)
                incoming.Dispose();

            foreach (var outgoing in _outgoing)
                outgoing.Dispose();

        }

        public class IncomingConnectionRejectionInfo
        {
            public string Reason { get; set; }
            public DateTime When { get; } = DateTime.UtcNow;
        }

        public class ConnectionFailureInfo
        {
            public const int MaxConnectionTimout = 60000;

            public int ErrorCount { get; set; }

            public TimeSpan NextTimout { get; set; } = TimeSpan.FromMilliseconds(500);

            public DateTime RetryOn { get; set; }

            public ReplicationDestination Destination { get; set; }

            public void Reset()
            {
                NextTimout = TimeSpan.FromMilliseconds(500);
                ErrorCount = 0;
            }

            public void OnError()
            {
                ErrorCount++;
                NextTimout = TimeSpan.FromMilliseconds(Math.Min(NextTimout.TotalMilliseconds * 4, MaxConnectionTimout));
                RetryOn = DateTime.UtcNow + NextTimout;
            }
        }
    }
}
