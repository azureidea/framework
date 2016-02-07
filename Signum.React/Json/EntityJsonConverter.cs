﻿using Newtonsoft.Json;
using Signum.Engine;
using Signum.Engine.Basics;
using Signum.Engine.Maps;
using Signum.Entities;
using Signum.Entities.Reflection;
using Signum.Utilities;
using Signum.Utilities.ExpressionTrees;
using Signum.Utilities.Reflection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Web;

namespace Signum.React.Json
{
    public class PropertyConverter
    {
        public static ConcurrentDictionary<Type, Dictionary<string, PropertyConverter>> PropertyConverters = new ConcurrentDictionary<Type, Dictionary<string, PropertyConverter>>();

        public static Dictionary<string, PropertyConverter> GetPropertyConverters(Type type)
        {
            return PropertyConverters.GetOrAdd(type, _t =>
            Validator.GetPropertyValidators(_t).Values.Select(pv => new PropertyConverter(_t, pv)).ToDictionary(a => a.PropertyValidator.PropertyInfo.Name.FirstLower()));
        }

        public readonly IPropertyValidator PropertyValidator;
        public readonly Func<object, object> GetValue;
        public readonly Action<object, object> SetValue;

        public PropertyConverter(Type type, IPropertyValidator pv)
        {
            this.PropertyValidator = pv;
            GetValue = ReflectionTools.CreateGetterUntyped(type, pv.PropertyInfo);
            SetValue = ReflectionTools.CreateSetterUntyped(type, pv.PropertyInfo);
        }

        public override string ToString()
        {
            return this.PropertyValidator.PropertyInfo.Name;
        }
    }


    public class EntityJsonConverter : JsonConverter
    {

        public override bool CanConvert(Type objectType)
        {
            return typeof(ModifiableEntity).IsAssignableFrom(objectType);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var pr = JsonSerializerExtensions.CurrentPropertyRoute ?? PropertyRoute.Root(value.GetType());

            ModifiableEntity mod = (ModifiableEntity)value;

            writer.WriteStartObject();

            var entity = mod as Entity;
            if (entity != null)
            {
                writer.WritePropertyName("Type");
                serializer.Serialize(writer, TypeLogic.TryGetCleanName(mod.GetType()));

                writer.WritePropertyName("id");
                serializer.Serialize(writer, entity.IdOrNull == null ? null : entity.Id.Object);
                
                if (Schema.Current.Table(entity.GetType()).Ticks != null)
                {
                    writer.WritePropertyName("ticks");
                    serializer.Serialize(writer, entity.Ticks);
                }
            }
            else
            {
                writer.WritePropertyName("Type");
                serializer.Serialize(writer, mod.GetType().Name);
            }

            if (!(mod is MixinEntity))
            {
                writer.WritePropertyName("toStr");
                serializer.Serialize(writer, mod.ToString());
            }

            foreach (var kvp in PropertyConverter.GetPropertyConverters(value.GetType()))
            {
                WriteProperty(writer, serializer, mod, kvp.Key, kvp.Value, pr);
            }

            if (entity != null && entity.Mixins.Any())
            {
                writer.WritePropertyName("mixins");
                writer.WriteStartObject();

                foreach (var m in entity.Mixins)
                {
                    var prm = pr.Add(m.GetType());
                  
                    using (JsonSerializerExtensions.SetCurrentPropertyRoute(prm))
                    {
                        writer.WritePropertyName(m.GetType().Name);
                        serializer.Serialize(writer, m);
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }


        public static Func<PropertyRoute, string> CanReadPropertyRoute;

        public virtual void WriteProperty(JsonWriter writer, JsonSerializer serializer, ModifiableEntity mod, string lowerCaseName, PropertyConverter pv, PropertyRoute route)
        {
            var pr = route.Add(pv.PropertyValidator.PropertyInfo);

            string error = CanReadPropertyRoute?.Invoke(pr);

            if (error != null)
                return;

            using (JsonSerializerExtensions.SetCurrentPropertyRoute(pr))
            {
                writer.WritePropertyName(lowerCaseName);
                serializer.Serialize(writer, pv.GetValue(mod));
            }
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            reader.Assert(JsonToken.StartObject);

            ModifiableEntity entity = GetEntity(reader, objectType, existingValue, serializer);

            var pr = JsonSerializerExtensions.CurrentPropertyRoute ?? PropertyRoute.Root(entity.GetType());

            var dic = PropertyConverter.GetPropertyConverters(entity.GetType());

            while (reader.TokenType == JsonToken.PropertyName)
            {
                PropertyConverter pc = dic.GetOrThrow((string)reader.Value);

                reader.Read();
                SetProperty(reader, serializer, entity, pc, pr);

                reader.Read();
            }

            reader.Assert(JsonToken.EndObject);

            return entity;
        }

        

        public virtual void SetProperty(JsonReader reader, JsonSerializer serializer, ModifiableEntity entity, PropertyConverter pc, PropertyRoute parentRoute)
        {
            object oldValue = pc.GetValue(entity);

            var pi = pc.PropertyValidator.PropertyInfo;

            var pr = parentRoute.Add(pi);

            AssertCanWrite(pr);
            using (JsonSerializerExtensions.SetCurrentPropertyRoute(pr))
            {
                object newValue = serializer.DeserializeValue(reader, pi.PropertyType, oldValue);

                pc.SetValue(entity, newValue);
            }
        }

        public static Func<PropertyRoute, string> CanWritePropertyRoute;
        public static void AssertCanWrite(PropertyRoute pr)
        {
            string error = CanWritePropertyRoute?.Invoke(pr);
            if (error != null)
                throw new UnauthorizedAccessException(error);
        }

        public virtual ModifiableEntity GetEntity(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            IdentityInfo identityInfo = ReadIdentityInfo(reader);

            identityInfo.AssertIsNewId(reader.Path);
            
            Type type = GetEntityType(identityInfo.Type, objectType);

            if (identityInfo.IsNew == true)
                return (ModifiableEntity)Activator.CreateInstance(type);

            if(typeof(Entity).IsAssignableFrom(type))
            {
                if (identityInfo.Id == null)
                    throw new JsonSerializationException($"Missing Id and IsNew for {identityInfo} ({reader.Path})");

           
                var id = PrimaryKey.Parse(identityInfo.Id, type);
                if (existingValue != null && existingValue.GetType() == type)
                {
                    Entity existingEntity = (Entity)existingValue;
                    if (existingEntity.Id == id)
                    {
                        if (identityInfo.Ticks != null)
                            existingEntity.Ticks = identityInfo.Ticks.Value;

                        return existingEntity;
                    }
                }

                var entity = Database.Retrieve(type, id);
                if (identityInfo.Ticks != null)
                    entity.Ticks = identityInfo.Ticks.Value;

                return entity;
            }
            else //Embedded
            {
                if (existingValue == null)
                    throw new JsonSerializationException($"Missing IsNew for {identityInfo} because existingValue is null");

                if (existingValue.GetType() != type)
                    throw new JsonSerializationException($"Missing IsNew for {identityInfo} because existingValue has a different type");

                return (ModifiableEntity)existingValue;              
            }
        }

        public virtual IdentityInfo ReadIdentityInfo(JsonReader reader)
        {
            IdentityInfo info = new IdentityInfo();
            reader.Read();
            while (reader.TokenType == JsonToken.PropertyName)
            {
                switch ((string)reader.Value)
                {
                    case "toStr": info.ToStr = reader.ReadAsString(); break;
                    case "id": info.Id = reader.ReadAsString(); break;
                    case "isNew": info.IsNew = reader.ReadAsBoolean(); break;
                    case "Type": info.Type = reader.ReadAsString(); break;
                    case "ticks": info.Ticks = long.Parse(reader.ReadAsString()); break;
                    default: return info;
                }

                reader.Read();
            }

            if (info.Type == null)
                throw new JsonSerializationException($"Expected member 'Type' not found in {reader.Path}");

            return info;
        }

        public struct IdentityInfo
        {
            public string Id;
            public bool? IsNew;
            public string Type;
            public string ToStr;
            public long? Ticks;

            public void AssertIsNewId(string path)
            {
                if (IsNew == true && Id != null)
                    throw new JsonSerializationException($"An entity of type '{ToStr}' is new but has id '({path})'");
            }

            public override string ToString()
            {
                var newOrId = IsNew == true ? "New" : Id;

                if (Ticks != null)
                    newOrId += $" (Ticks {Ticks})";

                return $"{Type} {newOrId}: {ToStr}";
            }
        }

        public virtual Type GetEntityType(string typeStr, Type objectType)
        {
            var type = TypeLogic.TryGetType(typeStr);
            if (type == null)
            {
                if (typeStr == objectType.Name)
                    throw new JsonSerializationException($"Type '{typeStr}' is not an Entity and is not the expected type ('{objectType.TypeName()}')");

                return objectType;
            }
            else
            {
                if (!objectType.IsAssignableFrom(type))
                    throw new JsonSerializationException($"Type '{type.Name}' is not assignable to '{objectType.TypeName()}'");

                return type;
            }
        }
    }
}