﻿using System;
using System.Collections.Generic;
using System.Data.Services.Providers;
using System.Linq;
using System.Text;
using DataServiceProvider;
using MongoDB.Bson;

namespace Mongo.Context
{
    internal static class MongoDSPConverter
    {
        private static DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static DSPResource CreateDSPResource(BsonDocument document, MongoMetadata mongoMetadata, string resourceName, string ownerPrefix = null)
        {
            var resourceType = mongoMetadata.ResolveResourceType(resourceName, ownerPrefix);
            if (resourceType == null)
                throw new ArgumentException(string.Format("Unable to resolve resource type {0}", resourceName), "resourceName");
            var resource = new DSPResource(resourceType);

            foreach (var element in document.Elements)
            {
                var resourceProperty = mongoMetadata.ResolveResourceProperty(resourceType, element);
                if (resourceProperty == null)
                    continue;

                object propertyValue = ConvertBsonValue(element.Value, resourceType, resourceProperty, resourceProperty.Name, mongoMetadata);
                resource.SetValue(resourceProperty.Name, propertyValue);
            }
            AssignNullCollections(resource, resourceType);

            return resource;
        }

        public static BsonDocument CreateBSonDocument(DSPResource resource, MongoMetadata mongoMetadata, string resourceName)
        {
            var document = new BsonDocument();
            var resourceSet = mongoMetadata.ResolveResourceSet(resourceName);
            if (resourceSet != null)
            {
                foreach (var property in resourceSet.ResourceType.Properties)
                {
                    var propertyValue = resource.GetValue(property.Name);
                    if (propertyValue != null)
                    {
                        document.Set(property.Name, BsonValue.Create(propertyValue));
                    }
                }
            }
            return document;
        }

        private static object ConvertBsonValue(BsonValue bsonValue, ResourceType resourceType, ResourceProperty resourceProperty, string propertyName, MongoMetadata mongoMetadata)
        {
            if (bsonValue == null)
                return null;

            object propertyValue = null;
            bool convertValue;

            if (bsonValue.GetType() == typeof(BsonDocument))
            {
                var bsonDocument = bsonValue.AsBsonDocument;
                if (IsCsharpNullDocument(bsonDocument))
                {
                    convertValue = false;
                }
                else
                {
                    propertyValue = CreateDSPResource(bsonDocument, mongoMetadata, propertyName,
                        MongoMetadata.GetQualifiedTypePrefix(resourceType.Name));
                    convertValue = true;
                }
            }
            else if (bsonValue.GetType() == typeof(BsonArray))
            {
                var bsonArray = bsonValue.AsBsonArray;
                if (bsonArray != null && bsonArray.Count > 0)
                    propertyValue = ConvertBsonArray(bsonArray, resourceType, propertyName, mongoMetadata);
                convertValue = false;
            }
            else if (bsonValue.GetType() == typeof(BsonNull) && resourceProperty.Kind == ResourcePropertyKind.Collection)
            {
                propertyValue = ConvertBsonArray(new BsonArray(0), resourceType, propertyName, mongoMetadata);
                convertValue = false;
            }
            else
            {
                propertyValue = ConvertRawValue(bsonValue);
                convertValue = true;
            }

            if (propertyValue != null && convertValue)
            {
                var propertyType = resourceProperty.ResourceType.InstanceType;
                Type underlyingNonNullableType = Nullable.GetUnderlyingType(resourceProperty.ResourceType.InstanceType);
                if (underlyingNonNullableType != null)
                {
                    propertyType = underlyingNonNullableType;
                }
                propertyValue = Convert.ChangeType(propertyValue, propertyType);
            }

            return propertyValue;
        }

        private static object ConvertBsonArray(BsonArray bsonArray, ResourceType resourceType, string propertyName, MongoMetadata mongoMetadata)
        {
            if (bsonArray == null || bsonArray.Count == 0)
            {
                return new object[0];
            }

            bool isDocument = false;
            int nonNullItemCount = 0;
            for (int index = 0; index < bsonArray.Count; index++)
            {
                if (bsonArray[index] != BsonNull.Value)
                {
                    if (bsonArray[index].GetType() == typeof(BsonDocument))
                        isDocument = true;
                    ++nonNullItemCount;
                }
            }
            object[] propertyValue = isDocument ? new DSPResource[nonNullItemCount] : new object[nonNullItemCount];
            int valueIndex = 0;
            for (int index = 0; index < bsonArray.Count; index++)
            {
                if (bsonArray[index] != BsonNull.Value)
                {
                    if (isDocument)
                    {
                        propertyValue[valueIndex++] = CreateDSPResource(bsonArray[index].AsBsonDocument, mongoMetadata,
                                                                     propertyName,
                                                                     MongoMetadata.GetQualifiedTypePrefix(resourceType.Name));
                    }
                    else
                    {
                        propertyValue[valueIndex++] = ConvertRawValue(bsonArray[index]);
                    }
                }
            }
            return propertyValue;
        }

        private static object ConvertRawValue(BsonValue bsonValue)
        {
            if (bsonValue == null)
                return null;

            if (BsonTypeMapper.MapToDotNetValue(bsonValue) != null)
            {
                if (bsonValue.IsObjectId)
                {
                    return bsonValue.ToString();
                }
                else if (bsonValue.IsGuid)
                {
                    return bsonValue.AsGuid;
                }
                else
                {
                    switch (bsonValue.BsonType)
                    {
                        case BsonType.DateTime:
                            return UnixEpoch + TimeSpan.FromMilliseconds(bsonValue.AsBsonDateTime.MillisecondsSinceEpoch);
                        default:
                            return BsonTypeMapper.MapToDotNetValue(bsonValue);
                    }
                }
            }
            else
            {
                switch (bsonValue.BsonType)
                {
                    case BsonType.Binary:
                        return bsonValue.AsBsonBinaryData.Bytes;
                    default:
                        return BsonTypeMapper.MapToDotNetValue(bsonValue);
                }
            }
        }

        private static void AssignNullCollections(DSPResource resource, ResourceType resourceType)
        {
            foreach (var resourceProperty in resourceType.Properties)
            {
                var propertyValue = resource.GetValue(resourceProperty.Name);
                if (resourceProperty.Kind == ResourcePropertyKind.Collection)
                {
                    if (propertyValue == null)
                    {
                        resource.SetValue(resourceProperty.Name, new object[0]);
                    }
                }
                else if (propertyValue is DSPResource)
                {
                    AssignNullCollections(propertyValue as DSPResource, resourceProperty.ResourceType);
                }
            }
        }

        private static bool IsCsharpNullDocument(BsonDocument bsonDocument)
        {
            if (bsonDocument.ElementCount == 1)
            {
                var element = bsonDocument.Elements.First();
                return element.Name == "_csharpnull" && element.Value.AsBoolean == true;
            }
            return false;
        }
    }
}
