﻿//
// JavaScriptSerializer.cs
//
// Author:
//   Konstantin Triger <kostat@mainsoft.com>
//
// (C) 2007 Mainsoft, Inc.  http://www.mainsoft.com
//
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
namespace Nancy.Json
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.IO;
    using System.Collections;
    using System.Reflection;
    using System.ComponentModel;
    using Nancy.Helpers;

    public class JavaScriptSerializer
    {
        internal const string SerializedTypeNameKey = "__type";

        List<IEnumerable<JavaScriptConverter>> _converterList;
        List<IEnumerable<JavaScriptPrimitiveConverter>> _primitiveConverterList;
        int _maxJsonLength;
        int _recursionLimit;
        bool _retainCasing;
        JavaScriptTypeResolver _typeResolver;
        bool _iso8601DateFormat;

#if NET_3_5
        internal static readonly JavaScriptSerializer DefaultSerializer = new JavaScriptSerializer(null, false, 2097152, 100);

        public JavaScriptSerializer()
            : this(null, false, 2097152, 100)
        {
        }

        public JavaScriptSerializer(JavaScriptTypeResolver resolver)
            : this(resolver, false, 2097152, 100)
        {
        }
#else
        internal static readonly JavaScriptSerializer DefaultSerializer = new JavaScriptSerializer(null, false, 102400, 100, false, true);

        public JavaScriptSerializer()
            : this(null, false, 102400, 100, false, true)
        {
        }

        public JavaScriptSerializer(JavaScriptTypeResolver resolver)
            : this(resolver, false, 102400, 100, false, true)
        {
        }
#endif
        public JavaScriptSerializer(JavaScriptTypeResolver resolver, bool registerConverters, int maxJsonLength, int recursionLimit, bool retainCasing, bool iso8601DateFormat)
        {
            _typeResolver = resolver;
            _maxJsonLength = maxJsonLength;
            _recursionLimit = recursionLimit;

            this.RetainCasing = retainCasing;

            _iso8601DateFormat = iso8601DateFormat;

            if (registerConverters)
                RegisterConverters(JsonSettings.Converters, JsonSettings.PrimitiveConverters);
        }


        public int MaxJsonLength
        {
            get
            {
                return _maxJsonLength;
            }
            set
            {
                _maxJsonLength = value;
            }
        }

        public int RecursionLimit
        {
            get
            {
                return _recursionLimit;
            }
            set
            {
                _recursionLimit = value;
            }
        }

        public bool ISO8601DateFormat
        {
            get { return _iso8601DateFormat; }
            set { _iso8601DateFormat = value; }
        }

        internal JavaScriptTypeResolver TypeResolver
        {
            get { return _typeResolver; }
        }

        public bool RetainCasing
        {
            get { return this._retainCasing; }
            set { this._retainCasing = value; }
        }

        public T ConvertToType<T>(object obj)
        {
            if (obj == null)
                return default(T);

            return (T)ConvertToType(typeof(T), obj);
        }

        internal object ConvertToType(Type type, object obj)
        {
            var primitiveConverter = GetPrimitiveConverter(type);

            if (primitiveConverter != null)
                obj = primitiveConverter.Deserialize(obj, type, this);

            if (obj == null)
                return null;

            if (obj is IDictionary<string, object>)
            {
                if (type == null)
                    obj = EvaluateDictionary((IDictionary<string, object>)obj);
                else
                {
                    JavaScriptConverter converter = GetConverter(type);
                    if (converter != null)
                        return converter.Deserialize(
                            EvaluateDictionary((IDictionary<string, object>)obj),
                            type, this);
                }

                return ConvertToObject((IDictionary<string, object>)obj, type);
            }
            if (obj is ArrayList)
                return ConvertToList((ArrayList)obj, type);

            if (type == null)
                return obj;

            Type sourceType = obj.GetType();
            if (type.IsAssignableFrom(sourceType))
                return obj;

            if (type.IsEnum)
                return ConvertToEnum(obj, type);

            TypeConverter c = TypeDescriptor.GetConverter(type);
            if (c.CanConvertFrom(sourceType))
            {
                if (obj is string)
                    return c.ConvertFromInvariantString((string)obj);

                return c.ConvertFrom(obj);
            }


            if (type == typeof(DateTimeOffset) && obj is string)
            {
                return DateTimeOffset.Parse((string)obj);
            }

            if ((type.IsGenericType) && (type.GetGenericTypeDefinition() == typeof(Nullable<>)))
            {
                /*
                 * Take care of the special case whereas in JSON an empty string ("") really means 
                 * an empty value 
                 * (see: https://bugzilla.novell.com/show_bug.cgi?id=328836)
                 */
                string s = obj as String;
                if (s != null)
                {
                    if (s == string.Empty)
                        return null;
                }

                var underlyingType = type.GetGenericArguments()[0];

                if (underlyingType.IsEnum)
                    return ConvertToEnum(obj, underlyingType);

                return Convert.ChangeType(obj, underlyingType);
            }

            return Convert.ChangeType(obj, type);
        }

        public T Deserialize<T>(string input)
        {
            return ConvertToType<T>(DeserializeObjectInternal(input));
        }

        static object Evaluate(object value)
        {
            return Evaluate(value, false);
        }

        static object Evaluate(object value, bool convertListToArray)
        {
            if (value is IDictionary<string, object>)
                value = EvaluateDictionary((IDictionary<string, object>)value, convertListToArray);
            else if (value is ArrayList)
                value = EvaluateList((ArrayList)value, convertListToArray);
            return value;
        }

        static object EvaluateList(ArrayList e)
        {
            return EvaluateList(e, false);
        }

        static object EvaluateList(ArrayList e, bool convertListToArray)
        {
            ArrayList list = new ArrayList();
            foreach (object value in e)
                list.Add(Evaluate(value, convertListToArray));

            return convertListToArray ? (object)list.ToArray() : list;
        }

        static IDictionary<string, object> EvaluateDictionary(IDictionary<string, object> dict)
        {
            return EvaluateDictionary(dict, false);
        }

        static IDictionary<string, object> EvaluateDictionary(IDictionary<string, object> dict, bool convertListToArray)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> entry in dict)
            {
                d.Add(entry.Key, Evaluate(entry.Value, convertListToArray));
            }

            return d;
        }

        static readonly Type typeofObject = typeof(object);
        static readonly Type typeofGenList = typeof(List<>);


        object ConvertToList(ArrayList col, Type type)
        {
            Type elementType = null;
            if (type != null && type.HasElementType)
                elementType = type.GetElementType();

            IList list;
            if (type == null || type.IsArray || typeofObject == type || typeof(ArrayList).IsAssignableFrom(type))
                list = new ArrayList();
            else if (ReflectionUtils.IsInstantiatableType(type))
                // non-generic typed list
                list = (IList)Activator.CreateInstance(type, true);
            else if (ReflectionUtils.IsAssignable(type, typeofGenList))
            {
                if (type.IsGenericType)
                {
                    Type[] genArgs = type.GetGenericArguments();
                    elementType = genArgs[0];
                    // generic list
                    list = (IList)Activator.CreateInstance(typeofGenList.MakeGenericType(genArgs));
                }
                else
                    list = new ArrayList();
            }
            else
                throw new InvalidOperationException(String.Format("Deserializing list type '{0}' not supported.", type.GetType().Name));

            if (list.IsReadOnly)
            {
                EvaluateList(col);
                return list;
            }

            foreach (object value in col)
                list.Add(ConvertToType(elementType, value));

            if (type != null && type.IsArray)
                list = ((ArrayList)list).ToArray(elementType);

            return list;
        }

        object ConvertToObject(IDictionary<string, object> dict, Type type)
        {
            if (_typeResolver != null)
            {
                if (dict.Keys.Contains(SerializedTypeNameKey))
                {
                    // already Evaluated
                    type = _typeResolver.ResolveType((string)dict[SerializedTypeNameKey]);
                }
            }

            var isDictionaryWithGuidKey = false;
            if (type.IsGenericType)
            {
                var genericTypeDefinition = type.GetGenericTypeDefinition();
                if (genericTypeDefinition.IsAssignableFrom(typeof(IDictionary<,>)) || genericTypeDefinition.GetInterfaces().Any(i => i == typeof(IDictionary)))
                {
                    Type[] arguments = type.GetGenericArguments();
                    if (arguments == null || arguments.Length != 2 || (arguments[0] != typeof(object) && arguments[0] != typeof(string) && arguments[0] != typeof(Guid)))
                        throw new InvalidOperationException(
                            "Type '" + type + "' is not supported for serialization/deserialization of a dictionary, keys must be strings, guids or objects.");
                    if (type.IsAbstract)
                    {
                        Type dictType = typeof(Dictionary<,>);
                        type = dictType.MakeGenericType(arguments[0], arguments[1]);
                    }

                    isDictionaryWithGuidKey = arguments[0] == typeof(Guid);
                }
                else
                {
                    var converter = GetConverter(genericTypeDefinition);
                    if (converter != null)
                        return converter.Deserialize(
                            EvaluateDictionary(dict),
                            type, this);
                }
            }
            else if (type.IsAssignableFrom(typeof(IDictionary)))
                type = typeof(Dictionary<string, object>);

            object target = Activator.CreateInstance(type, true);

            foreach (KeyValuePair<string, object> entry in dict)
            {
                object value = entry.Value;
                if (target is IDictionary)
                {
                    Type valueType = ReflectionUtils.GetTypedDictionaryValueType(type);
                    if (value != null && valueType == typeof(System.Object))
                        valueType = value.GetType();

                    if (isDictionaryWithGuidKey)
                    {
                        ((IDictionary)target).Add(new Guid(entry.Key), ConvertToType(valueType, value));
                    }
                    else
                    {
                        ((IDictionary)target).Add(entry.Key, ConvertToType(valueType, value));
                    }
                    continue;
                }
                MemberInfo[] memberCollection = type.GetMember(entry.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (memberCollection == null || memberCollection.Length == 0)
                {
                    //must evaluate value
                    Evaluate(value);
                    continue;
                }

                MemberInfo member = memberCollection[0];

                if (!ReflectionUtils.CanSetMemberValue(member))
                {
                    //must evaluate value
                    Evaluate(value);
                    continue;
                }

                Type memberType = ReflectionUtils.GetMemberUnderlyingType(member);

                if (memberType.IsInterface)
                {
                    if (memberType.IsGenericType)
                        memberType = ResolveGenericInterfaceToType(memberType);
                    else
                        memberType = ResolveInterfaceToType(memberType);

                    if (memberType == null)
                        throw new InvalidOperationException("Unable to deserialize a member, as its type is an unknown interface.");
                }

                ReflectionUtils.SetMemberValue(member, target, ConvertToType(memberType, value));
            }

            return target;
        }

        object ConvertToEnum(object obj, Type type)
        {
            if (obj is string)
                return Enum.Parse(type, (string)obj, true);
            else
                return Enum.ToObject(type, obj);
        }

        Type ResolveGenericInterfaceToType(Type type)
        {
            Type[] genericArgs = type.GetGenericArguments();

            if (ReflectionUtils.IsSubClass(type, typeof(IDictionary<,>)))
                return typeof(Dictionary<,>).MakeGenericType(genericArgs);

            if (ReflectionUtils.IsSubClass(type, typeof(IList<>)) ||
                ReflectionUtils.IsSubClass(type, typeof(ICollection<>)) ||
                ReflectionUtils.IsSubClass(type, typeof(IEnumerable<>))
            )
                return typeof(List<>).MakeGenericType(genericArgs);

            if (ReflectionUtils.IsSubClass(type, typeof(IComparer<>)))
                return typeof(Comparer<>).MakeGenericType(genericArgs);

            if (ReflectionUtils.IsSubClass(type, typeof(IEqualityComparer<>)))
                return typeof(EqualityComparer<>).MakeGenericType(genericArgs);

            return null;
        }

        Type ResolveInterfaceToType(Type type)
        {
            if (typeof(IDictionary).IsAssignableFrom(type))
                return typeof(Hashtable);

            if (typeof(IList).IsAssignableFrom(type) ||
                typeof(ICollection).IsAssignableFrom(type) ||
                typeof(IEnumerable).IsAssignableFrom(type))
                return typeof(ArrayList);

            if (typeof(IComparer).IsAssignableFrom(type))
                return typeof(Comparer);

            return null;
        }

        public object DeserializeObject(string input)
        {
            object obj = Evaluate(DeserializeObjectInternal(input), true);
            IDictionary dictObj = obj as IDictionary;
            if (dictObj != null && dictObj.Contains(SerializedTypeNameKey))
            {
                if (_typeResolver == null)
                {
                    throw new ArgumentNullException("resolver", "Must have a type resolver to deserialize an object that has an '__type' member");
                }

                obj = ConvertToType(null, obj);
            }
            return obj;
        }

        internal object DeserializeObjectInternal(string input)
        {
            return Json.Deserialize(input, this);
        }

        internal object DeserializeObjectInternal(TextReader input)
        {
            return Json.Deserialize(input, this);
        }

        public void RegisterConverters(IEnumerable<JavaScriptConverter> converters)
        {
            if (converters == null)
                throw new ArgumentNullException("converters");

            if (_converterList == null)
                _converterList = new List<IEnumerable<JavaScriptConverter>>();
            _converterList.Add(converters);
        }

        public void RegisterConverters(IEnumerable<JavaScriptPrimitiveConverter> primitiveConverters)
        {
            if (primitiveConverters == null)
                throw new ArgumentNullException("primitiveConverters");

            if (_primitiveConverterList == null)
                _primitiveConverterList = new List<IEnumerable<JavaScriptPrimitiveConverter>>();
            _primitiveConverterList.Add(primitiveConverters);
        }

        public void RegisterConverters(IEnumerable<JavaScriptConverter> converters, IEnumerable<JavaScriptPrimitiveConverter> primitiveConverters)
        {
            if (converters != null)
                RegisterConverters(converters);

            if (primitiveConverters != null)
                RegisterConverters(primitiveConverters);
        }

        internal JavaScriptConverter GetConverter(Type type)
        {
            if (_converterList != null)
                for (int i = 0; i < _converterList.Count; i++)
                {
                    foreach (JavaScriptConverter converter in _converterList[i])
                        foreach (Type supportedType in converter.SupportedTypes)
                            if (supportedType.IsAssignableFrom(type))
                                return converter;
                }

            return null;
        }

        internal JavaScriptPrimitiveConverter GetPrimitiveConverter(Type type)
        {
            if (_primitiveConverterList != null)
                for (int i = 0; i < _primitiveConverterList.Count; i++)
                {
                    foreach (JavaScriptPrimitiveConverter converter in _primitiveConverterList[i])
                        foreach (Type supportedType in converter.SupportedTypes)
                            if (supportedType.IsAssignableFrom(type))
                                return converter;
                }

            return null;
        }

        public string Serialize(object obj)
        {
            StringBuilder b = new StringBuilder();
            Serialize(obj, b);
            return b.ToString();
        }

        public void Serialize(object obj, StringBuilder output)
        {
            Json.Serialize(obj, this, output);
        }

        internal void Serialize(object obj, TextWriter output)
        {
            Json.Serialize(obj, this, output);
        }
    }
}
