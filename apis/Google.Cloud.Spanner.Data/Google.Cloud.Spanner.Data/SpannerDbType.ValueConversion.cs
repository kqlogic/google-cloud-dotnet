﻿// Copyright 2017 Google Inc. All Rights Reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections;
using System.Collections.Generic;
using static System.Globalization.CultureInfo;
using System.Linq;
using System.Reflection;
using System.Xml;
using Google.Protobuf.WellKnownTypes;
using TypeCode = Google.Cloud.Spanner.V1.TypeCode;

namespace Google.Cloud.Spanner.Data
{
    public sealed partial class SpannerDbType
    {
        internal object ConvertToClrType(Value protobufValue, SpannerConversionOptions options) =>
            ConvertToClrType(protobufValue, typeof(object), options, topLevel: true);

        internal T ConvertToClrType<T>(Value protobufValue, SpannerConversionOptions options) =>
            (T) ConvertToClrType(protobufValue, typeof(T), options, topLevel: true);

        // Visible only for test simplification reasons.
        internal object ConvertToClrType(Value protobufValue, System.Type targetClrType, SpannerConversionOptions options, bool topLevel)
        {
            if (protobufValue.KindCase == Value.KindOneofCase.NullValue)
            {
                bool targetIsNonNullableValueType =
                    targetClrType.GetTypeInfo().IsValueType && Nullable.GetUnderlyingType(targetClrType) == null;

                // Default behavior:
                // - Use DBNull.Value for top-level values
                // - Use null for array/struct elements where feasible
                // - Throw an exception when trying to convert a null array/struct element to a non-nullable value type
                if (options.UseDBNull)
                {
                    // No check for the target type. This matches the behavior of SqlDbDataReader etc,
                    // where calling GetString (etc) for a null value will throw an InvalidCastException.
                    if (topLevel)
                    {
                        return DBNull.Value;
                    }
                    if (targetIsNonNullableValueType)
                    {
                        throw new InvalidCastException($"Unable to convert null value to {targetClrType.Name}");
                    }
                    return null;
                }

                // 1.0 behavior: always just use the default value for the type, which is null for any reference
                // type or nullable value type, and the result of calling the parameterless constructor for
                // non-nullable value types.
                return targetIsNonNullableValueType ? Activator.CreateInstance(targetClrType) : null;
            }
            if (targetClrType == typeof(object))
            {
                //then we decide the type for you
                targetClrType = DefaultClrType;
            }
            var possibleUnderlyingType = Nullable.GetUnderlyingType(targetClrType);
            if (possibleUnderlyingType != null)
            {
                targetClrType = possibleUnderlyingType;
            }
            //extra supported conversions that are modifications of the "core" versions but may have loss of precision.
            //we call the spannerType with the known supported version and cast it down to lose precision.
            if (targetClrType == typeof(int))
            {
                return Convert.ToInt32(ConvertToClrTypeImpl<long>(protobufValue, options));
            }

            if (targetClrType == typeof(uint))
            {
                return Convert.ToUInt32(ConvertToClrTypeImpl<long>(protobufValue, options));
            }

            if (targetClrType == typeof(short))
            {
                return Convert.ToInt16(ConvertToClrTypeImpl<long>(protobufValue, options));
            }

            if (targetClrType == typeof(ushort))
            {
                return Convert.ToUInt16(ConvertToClrTypeImpl<long>(protobufValue, options));
            }

            if (targetClrType == typeof(sbyte))
            {
                return Convert.ToSByte(ConvertToClrTypeImpl<long>(protobufValue, options));
            }

            if (targetClrType == typeof(byte))
            {
                return Convert.ToByte(ConvertToClrTypeImpl<long>(protobufValue, options));
            }

            if (targetClrType == typeof(float))
            {
                return Convert.ToSingle(ConvertToClrTypeImpl<double>(protobufValue, options));
            }

            if (targetClrType == typeof(Guid))
            {
                return Guid.Parse(ConvertToClrTypeImpl<string>(protobufValue, options));
            }

            return ConvertToClrTypeImpl(protobufValue, targetClrType, options);
        }

        // Note: the options can *currently* be null because we're not using them, but
        // every call site should check that it could provide options if they become required.
        internal Value ToProtobufValue(object value, SpannerConversionOptions options)
        {
            if (value == null || value is DBNull)
            {
                return Value.ForNull();
            }

            switch (TypeCode)
            {
                case TypeCode.Bytes:
                    if (value is string s)
                    {
                        return new Value {StringValue = s};
                    }
                    if (value is byte[] bArray)
                    {
                        return new Value {StringValue = Convert.ToBase64String(bArray)};
                    }
                    throw new ArgumentException("TypeCode.Bytes only supports string and byte[]", nameof(value));
                case TypeCode.Bool:
                    return new Value {BoolValue = Convert.ToBoolean(value)};
                case TypeCode.String:
                    if (value is DateTime dateTime)
                    {
                        // If the value is a DateTime, we always convert using XmlConvert.
                        // This allows us to convert back to a datetime reliably from the
                        // resulting string (so roundtrip works properly if the developer uses
                        // a string as a backing field for a datetime for whatever reason).
                        return new Value { StringValue = XmlConvert.ToString(dateTime, XmlDateTimeSerializationMode.Utc) };
                    }
                    return new Value { StringValue = Convert.ToString(value, InvariantCulture) };
                case TypeCode.Int64:
                        return new Value { StringValue = Convert.ToInt64(value, InvariantCulture)
                            .ToString(InvariantCulture) };
                case TypeCode.Float64:
                    return new Value {NumberValue = Convert.ToDouble(value, InvariantCulture)};
                case TypeCode.Timestamp:
                    return new Value
                    {
                        StringValue = XmlConvert.ToString(Convert.ToDateTime(value, InvariantCulture), XmlDateTimeSerializationMode.Utc)
                    };
                case TypeCode.Date:
                    return new Value
                    {
                        StringValue = StripTimePart(
                            XmlConvert.ToString(Convert.ToDateTime(value, InvariantCulture), XmlDateTimeSerializationMode.Utc))
                    };
                case TypeCode.Array:
                    if (value is IEnumerable enumerable)
                    {
                        return Value.ForList(
                            enumerable.Cast<object>()
                                .Select(x => ArrayElementType.ToProtobufValue(x, options)).ToArray());
                    }
                    throw new ArgumentException("The given array instance needs to implement IEnumerable.");
                case TypeCode.Struct:
                    if (value is IDictionary dictionary)
                    {
                        var structValue = new Struct();
                        foreach (var key in dictionary.Keys)
                        {
                            string keyString = Convert.ToString(key, InvariantCulture);
                            if (!StructMembers.ContainsKey(keyString))
                            {
                                throw new ArgumentException("The given struct instance has members not defined in the Struct.", nameof(value));
                            }
                            structValue.Fields[keyString] = StructMembers[keyString].ToProtobufValue(dictionary[key], options);
                        }
                        return Value.ForStruct(structValue);
                    }
                    throw new ArgumentException("The given struct instance needs to implement IDictionary.");

                default:
                    throw new ArgumentOutOfRangeException(nameof(TypeCode), TypeCode, null);
            }
        }

        private T ConvertToClrTypeImpl<T>(Value wireValue, SpannerConversionOptions options) => (T) ConvertToClrTypeImpl(wireValue, typeof(T), options);

        private object ConvertToClrTypeImpl(Value wireValue, System.Type targetClrType, SpannerConversionOptions options)
        {
            //If the wireValue itself is assignable to the target type, just return it
            //This covers both typeof(Value) and typeof(object).
            if (wireValue == null || targetClrType == null || targetClrType == typeof(Value))
            {
                return wireValue;
            }

            //targetClrType should be one of the values returned by GetDefaultClrTypeFromSpannerType
            if (targetClrType == typeof(bool))
            {
                switch (wireValue.KindCase)
                {
                    case Value.KindOneofCase.NullValue:
                        return default(bool);
                    case Value.KindOneofCase.StringValue:
                        if (TypeCode == TypeCode.Int64)
                        {
                            return Convert.ToBoolean(Convert.ToInt64(wireValue.StringValue, InvariantCulture));
                        }
                        return Convert.ToBoolean(wireValue.StringValue);
                    case Value.KindOneofCase.BoolValue:
                        return wireValue.BoolValue;
                    case Value.KindOneofCase.NumberValue:
                        return Convert.ToBoolean(wireValue.NumberValue);
                    default:
                        throw new InvalidOperationException(
                            $"Invalid Type conversion from {wireValue.KindCase} to {targetClrType.FullName}");
                }
            }

            if (targetClrType == typeof(char))
            {
                switch (wireValue.KindCase)
                {
                    case Value.KindOneofCase.BoolValue:
                        return Convert.ToChar(wireValue.BoolValue);
                    case Value.KindOneofCase.NumberValue:
                        return Convert.ToChar(wireValue.NumberValue);
                    case Value.KindOneofCase.NullValue:
                        return default(char);
                    case Value.KindOneofCase.StringValue:
                        if (TypeCode == TypeCode.Int64)
                        {
                            return Convert.ToChar(Convert.ToInt64(wireValue.StringValue, InvariantCulture));
                        }
                        return Convert.ToChar(wireValue.StringValue);
                    default:
                        throw new InvalidOperationException(
                            $"Invalid Type conversion from {wireValue.KindCase} to {targetClrType.FullName}");
                }
            }

            if (targetClrType == typeof(long))
            {
                switch (wireValue.KindCase)
                {
                    case Value.KindOneofCase.BoolValue:
                        return Convert.ToInt64(wireValue.BoolValue);
                    case Value.KindOneofCase.NumberValue:
                        return Convert.ToInt64(wireValue.NumberValue);
                    case Value.KindOneofCase.NullValue:
                        return default(long);
                    case Value.KindOneofCase.StringValue:
                        return Convert.ToInt64(wireValue.StringValue, InvariantCulture);
                    default:
                        throw new InvalidOperationException(
                            $"Invalid Type conversion from {wireValue.KindCase} to {targetClrType.FullName}");
                }
            }

            if (targetClrType == typeof(ulong))
            {
                switch (wireValue.KindCase)
                {
                    case Value.KindOneofCase.BoolValue:
                        return Convert.ToUInt64(wireValue.BoolValue);
                    case Value.KindOneofCase.NumberValue:
                        return Convert.ToUInt64(wireValue.NumberValue);
                    case Value.KindOneofCase.NullValue:
                        return default(ulong);
                    case Value.KindOneofCase.StringValue:
                        return Convert.ToUInt64(wireValue.StringValue, InvariantCulture);
                    default:
                        throw new InvalidOperationException(
                            $"Invalid Type conversion from {wireValue.KindCase} to {targetClrType.FullName}");
                }
            }

            if (targetClrType == typeof(decimal))
            {
                switch (wireValue.KindCase)
                {
                    case Value.KindOneofCase.BoolValue:
                        return Convert.ToDecimal(wireValue.BoolValue);
                    case Value.KindOneofCase.NumberValue:
                        return Convert.ToDecimal(wireValue.NumberValue);
                    case Value.KindOneofCase.NullValue:
                        return default(decimal);
                    case Value.KindOneofCase.StringValue:
                        return Convert.ToDecimal(wireValue.StringValue, InvariantCulture);
                    default:
                        throw new InvalidOperationException(
                            $"Invalid Type conversion from {wireValue.KindCase} to {targetClrType.FullName}");
                }
            }

            if (targetClrType == typeof(double))
            {
                switch (wireValue.KindCase)
                {
                    case Value.KindOneofCase.BoolValue:
                        return Convert.ToDouble(wireValue.BoolValue);
                    case Value.KindOneofCase.NullValue:
                        return default(double);
                    case Value.KindOneofCase.NumberValue:
                        return wireValue.NumberValue;
                    case Value.KindOneofCase.StringValue:
                        if (string.Compare(wireValue.StringValue, "NaN", StringComparison.OrdinalIgnoreCase) == 0)
                        {
                            return double.NaN;
                        }

                        if (string.Compare(wireValue.StringValue, "Infinity", StringComparison.OrdinalIgnoreCase) == 0)
                        {
                            return double.PositiveInfinity;
                        }

                        if (string.Compare(wireValue.StringValue, "-Infinity", StringComparison.OrdinalIgnoreCase) == 0)
                        {
                            return double.NegativeInfinity;
                        }

                        return Convert.ToDouble(wireValue.StringValue, InvariantCulture);
                    default:
                        throw new InvalidOperationException(
                            $"Invalid Type conversion from {wireValue.KindCase} to {targetClrType.FullName}");
                }
            }

            if (targetClrType == typeof(DateTime))
            {
                switch (wireValue.KindCase)
                {
                    case Value.KindOneofCase.NullValue:
                        return null;
                    case Value.KindOneofCase.StringValue:
                        return XmlConvert.ToDateTime(wireValue.StringValue, XmlDateTimeSerializationMode.Utc);
                    default:
                        throw new InvalidOperationException(
                            $"Invalid Type conversion from {wireValue.KindCase} to {targetClrType.FullName}");
                }
            }

            if (targetClrType == typeof(Timestamp))
            {
                switch (wireValue.KindCase)
                {
                    case Value.KindOneofCase.NullValue:
                        return null;
                    case Value.KindOneofCase.StringValue:
                        return Protobuf.WellKnownTypes.Timestamp.Parser.ParseJson(wireValue.StringValue);
                    default:
                        throw new InvalidOperationException(
                            $"Invalid Type conversion from {wireValue.KindCase} to {targetClrType.FullName}");
                }
            }

            if (targetClrType == typeof(string))
            {
                switch (wireValue.KindCase)
                {
                    case Value.KindOneofCase.NullValue:
                        return null;
                    case Value.KindOneofCase.NumberValue:
                        return wireValue.NumberValue.ToString(InvariantCulture);
                    case Value.KindOneofCase.StringValue:
                        return wireValue.StringValue;
                    case Value.KindOneofCase.BoolValue:
                        return wireValue.BoolValue.ToString();
                    default:
                        return wireValue.ToString();
                }
            }

            if (targetClrType == typeof(byte[]))
            {
                switch (wireValue.KindCase)
                {
                    case Value.KindOneofCase.NullValue:
                        return null;
                    case Value.KindOneofCase.StringValue:
                        return Convert.FromBase64String(wireValue.StringValue);
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            if (typeof(IDictionary).IsAssignableFrom(targetClrType))
            {
                if (targetClrType == typeof(IDictionary))
                {
                    targetClrType = typeof(Dictionary<string, object>);
                }
                //a bit of recursion here...
                IDictionary dictionary = (IDictionary)Activator.CreateInstance(targetClrType);
                var itemType = targetClrType.GetGenericArguments().Skip(1).FirstOrDefault() ?? typeof(object);
                switch (wireValue.KindCase)
                {
                    case Value.KindOneofCase.StructValue:
                        foreach (var structField in StructMembers)
                        {
                            var fieldValue = wireValue.StructValue.Fields[structField.Key];
                            dictionary[structField.Key] = structField.Value.ConvertToClrType(fieldValue, itemType, options, topLevel: false);
                        }

                        return dictionary;
                    case Value.KindOneofCase.ListValue:
                        if (TypeCode == TypeCode.Struct)
                        {
                            for (var i = 0; i < StructOrder?.Count; i++)
                            {
                                var elementValue = wireValue.ListValue.Values[i];
                                dictionary[StructOrder[i]] = StructMembers[StructOrder[i]].ConvertToClrType(elementValue, itemType, options, topLevel: false);
                            }
                        }
                        else
                        {
                            var i = 0;
                            foreach (var listItemValue in wireValue.ListValue.Values)
                            {
                                dictionary[i] = ArrayElementType.ConvertToClrType(listItemValue, itemType, options, topLevel: false);
                                i++;
                            }
                        }
                        return dictionary;
                    default:
                        throw new ArgumentException(
                            $"Invalid Type conversion from {wireValue.KindCase} to {targetClrType.FullName}");
                }
            }
            if (targetClrType.IsArray)
            {
                switch (wireValue.KindCase)
                {
                    case Value.KindOneofCase.NullValue: return null;
                    case Value.KindOneofCase.ListValue:
                        var newArray = Array.CreateInstance(
                            targetClrType.GetElementType(),
                            wireValue.ListValue.Values.Count);

                        var i = 0;
                        foreach (var obj in wireValue.ListValue.Values.Select(
                            x => ArrayElementType.ConvertToClrType(x, targetClrType.GetElementType(), options, topLevel: false)))
                        {
                            newArray.SetValue(obj, i);
                            i++;
                        }
                        return newArray;
                    default:
                        throw new InvalidOperationException(
                            $"Invalid Type conversion from {wireValue.KindCase} to {targetClrType.FullName}");
                }
            }
            if (typeof(IList).IsAssignableFrom(targetClrType))
            {
                if (targetClrType == typeof(IList))
                {
                    targetClrType = typeof(List<object>);
                }
                switch (wireValue.KindCase)
                {
                    case Value.KindOneofCase.NullValue: return null;
                    case Value.KindOneofCase.ListValue:
                        var newList = (IList) Activator.CreateInstance(targetClrType);
                        var itemType = targetClrType.GetGenericArguments().FirstOrDefault() ?? typeof(object);
                        foreach (var obj in wireValue.ListValue.Values.Select(
                            x => ArrayElementType.ConvertToClrType(x, itemType, options, topLevel: false)))
                        {
                            newList.Add(obj);
                        }
                        return newList;
                    default:
                        throw new InvalidOperationException(
                            $"Invalid Type conversion from {wireValue.KindCase} to {targetClrType.FullName}");
                }
            }
            throw new ArgumentException(
                $"Invalid Type conversion from {wireValue.KindCase} to {targetClrType.FullName}");
        }

        private static string StripTimePart(string rfc3339String)
        {
            if (!string.IsNullOrEmpty(rfc3339String))
            {
                int timeIndex = rfc3339String.IndexOf('T');
                if (timeIndex != -1)
                {
                    return rfc3339String.Substring(0, timeIndex);
                }
            }
            return rfc3339String;
        }
    }
}
