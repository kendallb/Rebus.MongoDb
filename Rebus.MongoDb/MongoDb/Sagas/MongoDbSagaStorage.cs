﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Rebus.Exceptions;
using Rebus.Sagas;
using MongoDB.Bson.Serialization;
using Rebus.Bus;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Sagas.Idempotent;
// ReSharper disable InconsistentlySynchronizedField

namespace Rebus.MongoDb.Sagas
{
    /// <summary>
    /// Implementation of <see cref="ISagaStorage"/> that uses MongoDB to store saga data
    /// </summary>
    public class MongoDbSagaStorage : ISagaStorage, IInitializable
    {
        /// <summary>
        /// Static lock object here to guard registration across all bus instances
        /// </summary>
        readonly ConcurrentDictionary<Type, Lazy<Func<Task>>> _collectionInitializers = new ConcurrentDictionary<Type, Lazy<Func<Task>>>();
        static readonly object ClassMapRegistrationLock = new object();
        readonly IMongoDatabase _mongoDatabase;
        readonly Func<Type, string> _collectionNameResolver;
        readonly ILog _log;

        /// <summary>
        /// Constructs the saga storage to use the given database. If specified, the given <paramref name="collectionNameResolver"/> will
        /// be used to get names for each type of saga data that needs to be persisted. By default, the saga data's <see cref="MemberInfo.Name"/>
        /// will be used.
        /// </summary>
        public MongoDbSagaStorage(IMongoDatabase mongoDatabase, IRebusLoggerFactory rebusLoggerFactory, Func<Type, string> collectionNameResolver = null)
        {
            if (rebusLoggerFactory == null) throw new ArgumentNullException(nameof(rebusLoggerFactory));
            _mongoDatabase = mongoDatabase ?? throw new ArgumentNullException(nameof(mongoDatabase));
            _collectionNameResolver = collectionNameResolver ?? (type => type.Name);
            _log = rebusLoggerFactory.GetLogger<MongoDbSagaStorage>();
        }

        /// <summary>
        /// Initializes the saga storage by registering necessary class maps
        /// </summary>
        public void Initialize()
        {
            RegisterClassMaps();
        }

        /// <inheritdoc />
        public async Task<ISagaData> Find(Type sagaDataType, string propertyName, object propertyValue)
        {
            var collection = await GetCollection(sagaDataType);

            var criteria = propertyName == nameof(ISagaData.Id)
                ? new BsonDocument { { "_id", BsonValue.Create(propertyValue) } }
                : new BsonDocument { { propertyName, BsonValue.Create(propertyValue) } };

            //var criteria = propertyName == nameof(ISagaData.Id)
            //    ? Builders<BsonDocument>.Filter.Eq("_id", propertyValue)
            //    : Builders<BsonDocument>.Filter.Eq(propertyName, propertyValue);

            var result = await collection.Find(criteria).FirstOrDefaultAsync().ConfigureAwait(false);

            if (result == null) return null;

            return (ISagaData)BsonSerializer.Deserialize(result, sagaDataType);
        }

        /// <inheritdoc />
        public async Task Insert(ISagaData sagaData, IEnumerable<ISagaCorrelationProperty> correlationProperties)
        {
            if (sagaData.Id == Guid.Empty)
            {
                throw new InvalidOperationException($"Attempted to insert saga data {sagaData.GetType()} without an ID");
            }

            if (sagaData.Revision != 0)
            {
                throw new InvalidOperationException($"Attempted to insert saga data with ID {sagaData.Id} and revision {sagaData.Revision}, but revision must be 0 on first insert!");
            }

            var collection = await GetCollection(sagaData.GetType(), correlationProperties);

            var document = sagaData.ToBsonDocument();

            await collection.InsertOneAsync(document).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task Update(ISagaData sagaData, IEnumerable<ISagaCorrelationProperty> correlationProperties)
        {
            var collection = await GetCollection(sagaData.GetType(), correlationProperties);

            var criteria = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", sagaData.Id),
                Builders<BsonDocument>.Filter.Eq(nameof(ISagaData.Revision), sagaData.Revision)
            );

            sagaData.Revision++;

            var result = await collection.ReplaceOneAsync(criteria, sagaData.ToBsonDocument(sagaData.GetType())).ConfigureAwait(false);

            if (!result.IsModifiedCountAvailable || result.ModifiedCount != 1)
            {
                throw new ConcurrencyException($"Saga data {sagaData.GetType()} with ID {sagaData.Id} in collection {collection.CollectionNamespace} could not be updated!");
            }
        }

        /// <inheritdoc />
        public async Task Delete(ISagaData sagaData)
        {
            var collection = await GetCollection(sagaData.GetType());

            var criteria = Builders<BsonDocument>.Filter.Eq("_id", sagaData.Id);

            var result = await collection.DeleteManyAsync(criteria).ConfigureAwait(false);

            if (result.DeletedCount != 1)
            {
                throw new ConcurrencyException($"Saga data {sagaData.GetType()} with ID {sagaData.Id} in collection {collection.CollectionNamespace} could not be deleted");
            }

            sagaData.Revision++;
        }

        async Task<IMongoCollection<BsonDocument>> GetCollection(Type sagaDataType, IEnumerable<ISagaCorrelationProperty> correlationProperties = null)
        {
            try
            {
                var collectionName = _collectionNameResolver(sagaDataType);
                var mongoCollection = _mongoDatabase.GetCollection<BsonDocument>(collectionName);

                if (correlationProperties == null) return mongoCollection;

                async Task CreateIndexes()
                {
                    _log.Info("Initializing index for saga data {type} in collection {collectionName}", sagaDataType, collectionName);

                    foreach (var correlationProperty in correlationProperties)
                    {
                        _log.Debug("Creating index on property {propertyName} of {type}", correlationProperty.PropertyName, sagaDataType);

                        var index = new BsonDocument { { correlationProperty.PropertyName, 1 } };
                        var indexDef = new BsonDocumentIndexKeysDefinition<BsonDocument>(index);
                        await mongoCollection.Indexes.CreateOneAsync(indexDef, new CreateIndexOptions { Unique = true });
                    }
                }

                var initializer = _collectionInitializers.GetOrAdd(sagaDataType, _ => new Lazy<Func<Task>>(() => CreateIndexes));

                await initializer.Value();

                return mongoCollection;
            }
            catch (Exception exception)
            {
                throw new RebusApplicationException(exception, $"Could not get MongoCollection for saga data of type {sagaDataType}");
            }
        }

        void RegisterClassMaps()
        {
            lock (ClassMapRegistrationLock)
            {
                if (BsonClassMap.IsClassMapRegistered(typeof(IdempotencyData)))
                {
                    _log.Debug("BSON class map for {type} already registered - not doing anything", typeof(IdempotencyData));
                    return;
                }

                _log.Debug("Registering BSON class maps for {type} and accompanying types", typeof(IdempotencyData));

                BsonClassMap.RegisterClassMap<IdempotencyData>(map =>
                {
                    map.MapCreator(obj => new IdempotencyData(obj.OutgoingMessages, obj.HandledMessageIds));
                    map.MapMember(obj => obj.HandledMessageIds);
                    map.MapMember(obj => obj.OutgoingMessages);
                });

                BsonClassMap.RegisterClassMap<OutgoingMessage>(map =>
                {
                    map.MapCreator(obj => new OutgoingMessage(obj.DestinationAddresses, obj.TransportMessage));
                    map.MapMember(obj => obj.DestinationAddresses);
                    map.MapMember(obj => obj.TransportMessage);
                });

                BsonClassMap.RegisterClassMap<OutgoingMessages>(map =>
                {
                    map.MapCreator(obj => new OutgoingMessages(obj.MessageId, obj.MessagesToSend));
                    map.MapMember(obj => obj.MessageId);
                    map.MapMember(obj => obj.MessagesToSend);
                });

                BsonClassMap.RegisterClassMap<TransportMessage>(map =>
                {
                    map.MapCreator(obj => new TransportMessage(obj.Headers, obj.Body));
                    map.MapMember(obj => obj.Headers);
                    map.MapMember(obj => obj.Body);
                });
            }
        }
    }
}
