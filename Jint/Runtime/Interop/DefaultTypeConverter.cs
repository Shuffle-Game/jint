﻿using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using Jint.Native;
using System.Collections.Generic;
using System.Dynamic;
using System.Reflection;

namespace Jint.Runtime.Interop
{
    public class JsList<T> : List<T>
    {
        public JsList()
        {
        }

        public void ForEach(Action<T, int> call)
        {
            for (int i = 0; i < this.Count; i++)
            {
                call(this[i], i);
            }
        }
        public JsList<T> Where(Func<T, bool> call)
        {
            JsList<T> items = new JsList<T>();

            foreach (var v in this)
            {
                if (call(v))
                {
                    items.Add(v);
                }
            }
            return items;
        }

        public bool Any(Func<T, bool> call)
        {
            JsList<T> items = new JsList<T>();

            foreach (var v in this)
            {
                if (call(v))
                {
                    return true;
                }
            }
            return false;
        }

        public void Splice(int index, int length)
        {
            for (int i = index + length - 1; i >= index; i--)
            {
                this.RemoveAt(i);
            }
        }
        public void Push(T item)
        {
            this.Add(item);
        }
        public int Length { get { return this.Count; } }
    }

    public class DefaultTypeConverter : ITypeConverter
    {
        private readonly Engine _engine;
        private static readonly Dictionary<string, bool> _knownConversions = new Dictionary<string, bool>();
        private static readonly object _lockObject = new object();

        private static MethodInfo convertChangeType = typeof(System.Convert).GetMethod("ChangeType", new Type[] { typeof(object), typeof(Type), typeof(IFormatProvider) });
        private static MethodInfo jsValueFromObject = typeof(JsValue).GetMethod("FromObject");
        private static MethodInfo jsValueToObject = typeof(JsValue).GetMethod("ToObject");

        public DefaultTypeConverter(Engine engine)
        {
            _engine = engine;
        }

        public virtual object Convert(object value, Type type, IFormatProvider formatProvider)
        {
            if (value == null)
            {
                if (TypeConverter.TypeIsNullable(type))
                {
                    return null;
                }

                throw new NotSupportedException(string.Format("Unable to convert null to '{0}'", type.FullName));
            }

            // don't try to convert if value is derived from type
            if (type.IsInstanceOfType(value))
            {
                return value;
            }

            var expando = value as ExpandoObject;
            if (expando != null)
            {
                var inst = Activator.CreateInstance(type);
                foreach (var kv in expando)
                {
                    var property = type.GetProperties(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public).Where(p => ObjectWrapper.EqualsIgnoreCasing(p.Name, kv.Key)).FirstOrDefault();
                    if (property != null)
                    {
                        property.SetValue(inst,kv.Value,null);
                    }
                    else
                    {
                        var field = type.GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public).Where(f => ObjectWrapper.EqualsIgnoreCasing(f.Name, kv.Key)).FirstOrDefault();
                        if (field != null)
                        {
                            field.SetValue(inst, kv.Value);
                        }
                    }

                }
                return inst;
            }

            if (type.IsEnum)
            {
                var integer = System.Convert.ChangeType(value, typeof(int), formatProvider);
                if (integer == null)
                {
                    throw new ArgumentOutOfRangeException();
                }

                return Enum.ToObject(type, integer);
            }

            var valueType = value.GetType();
            // is the javascript value an ICallable instance ?
            if (valueType == typeof(Func<JsValue, JsValue[], JsValue>))
            {
                var function = (Func<JsValue, JsValue[], JsValue>)value;

                if (type.IsGenericType)
                {
                    var genericType = type.GetGenericTypeDefinition();

                    // create the requested Delegate
                    if (genericType.Name.StartsWith("Action"))
                    {
                        var genericArguments = type.GetGenericArguments();

                        var @params = new ParameterExpression[genericArguments.Count()];
                        for (var i = 0; i < @params.Count(); i++)
                        {
                            @params[i] = Expression.Parameter(genericArguments[i], genericArguments[i].Name + i);
                        }

                        var engine = Expression.Constant(_engine, typeof (Engine));
                        var @vars = Expression.NewArrayInit(typeof(JsValue), @params.Select(p => Expression.Call(null, jsValueFromObject, engine, Expression.Convert(p, typeof(object)))));

                        var callExpresion = Expression.Block(Expression.Call(
                                                Expression.Call(Expression.Constant(function.Target),
                                                    function.Method,
                                                    Expression.Constant(JsValue.Undefined, typeof(JsValue)),
                                                    @vars),
                                                jsValueToObject), Expression.Empty());

                        return Expression.Lambda(callExpresion, new ReadOnlyCollection<ParameterExpression>(@params));
                    }
                    else if (genericType.Name.StartsWith("Func"))
                    {
                        var genericArguments = type.GetGenericArguments();
                        var returnType = genericArguments.Last();

                        var @params = new ParameterExpression[genericArguments.Count() - 1];
                        for (var i = 0; i < @params.Count(); i++)
                        {
                            @params[i] = Expression.Parameter(genericArguments[i], genericArguments[i].Name + i);
                        }

                        var @vars =
                            Expression.NewArrayInit(typeof(JsValue),
                                @params.Select(p =>
                                {
                                    var boxingExpression = Expression.Convert(p, typeof(object));
                                    return Expression.Call(null, jsValueFromObject, Expression.Constant(_engine, typeof(Engine)), boxingExpression);
                                })
                            );

                        // the final result's type needs to be changed before casting,
                        // for instance when a function returns a number (double) but C# expects an integer

                        var callExpresion = Expression.Convert(
                                                Expression.Call(null,
                                                    convertChangeType,
                                                    Expression.Call(
                                                            Expression.Call(Expression.Constant(function.Target),
                                                                    function.Method,
                                                                    Expression.Constant(JsValue.Undefined, typeof(JsValue)),
                                                                    @vars),
                                                            jsValueToObject),
                                                        Expression.Constant(returnType, typeof(Type)),
                                                        Expression.Constant(System.Globalization.CultureInfo.InvariantCulture, typeof(IFormatProvider))
                                                        ),
                                                    returnType);

                        return Expression.Lambda(callExpresion, new ReadOnlyCollection<ParameterExpression>(@params));
                    }
                }
                else
                {
                    if (type == typeof(Action))
                    {
                        return (Action)(() => function(JsValue.Undefined, new JsValue[0]));
                    }
                    else if (type.IsSubclassOf(typeof(System.MulticastDelegate)))
                    {
                        var method = type.GetMethod("Invoke");
                        var arguments = method.GetParameters();

                        var @params = new ParameterExpression[arguments.Count()];
                        for (var i = 0; i < @params.Count(); i++)
                        {
                            @params[i] = Expression.Parameter(typeof(object), arguments[i].Name);
                        }
                        var @vars = Expression.NewArrayInit(typeof(JsValue), @params.Select(p => Expression.Call(null, typeof(JsValue).GetMethod("FromObject"), Expression.Constant(_engine, typeof(Engine)), p)));

                        var callExpression = Expression.Block(
                                                Expression.Call(
                                                    Expression.Call(Expression.Constant(function.Target),
                                                        function.Method,
                                                        Expression.Constant(JsValue.Undefined, typeof(JsValue)),
                                                        @vars),
                                                    typeof(JsValue).GetMethod("ToObject")),
                                                Expression.Empty());

                        var dynamicExpression = Expression.Invoke(Expression.Lambda(callExpression, new ReadOnlyCollection<ParameterExpression>(@params)), new ReadOnlyCollection<ParameterExpression>(@params));

                        return Expression.Lambda(type, dynamicExpression, new ReadOnlyCollection<ParameterExpression>(@params));
                    }
                }

            }

            if (type.IsArray)
            {
                var source = value as object[];
                if (source == null)
                    throw new ArgumentException(String.Format("Value of object[] type is expected, but actual type is {0}.", value.GetType()));

                var targetElementType = type.GetElementType();
                var itemsConverted = source.Select(o => Convert(o, targetElementType, formatProvider)).ToArray();
                var result = Array.CreateInstance(targetElementType, source.Length);
                itemsConverted.CopyTo(result, 0);
                return result;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(JsList<>))
            {
                var items = (object[]) value;
                var finalType = type.GetGenericArguments()[0];
                var objArr = items;
                var arr = Array.CreateInstance(finalType, objArr.Length);
                Array.Copy(objArr, arr, objArr.Length);

                var cc = Activator.CreateInstance(type);
                cc.GetType().GetMethod("AddRange").Invoke(cc, new[] {arr});
                return cc;
            }

            try
            {
                return System.Convert.ChangeType(value, type, formatProvider);
            }
            catch (Exception ex)
            {
                throw new Exception("Cannot convert type " + value.GetType().FullName + " to type " + type.FullName);
            }

        }


        public virtual bool TryConvert(object value, Type type, IFormatProvider formatProvider, out object converted)
        {
            bool canConvert;
            var key = value == null ? String.Format("Null->{0}", type) : String.Format("{0}->{1}", value.GetType(), type);

            if (!_knownConversions.TryGetValue(key, out canConvert))
            {
                lock (_lockObject)
                {
                    if (!_knownConversions.TryGetValue(key, out canConvert))
                    {
                        try
                        {
                            converted = Convert(value, type, formatProvider);
                            _knownConversions.Add(key, true);
                            return true;
                        }
                        catch
                        {
                            converted = null;
                            _knownConversions.Add(key, false);
                            return false;
                        }
                    }
                }
            }

            if (canConvert)
            {
                converted = Convert(value, type, formatProvider);
                return true;
            }

            converted = null;
            return false;
        }
    }
}
