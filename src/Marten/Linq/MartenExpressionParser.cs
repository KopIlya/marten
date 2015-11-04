using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using FubuCore;
using FubuCore.Reflection;
using Marten.Schema;
using Marten.Util;

namespace Marten.Linq
{
    

    public static class MartenExpressionParser
    {
        private static readonly string CONTAINS = ReflectionHelper.GetMethod<string>(x => x.Contains("null")).Name;
        private static readonly string STARTS_WITH = ReflectionHelper.GetMethod<string>(x => x.StartsWith("null")).Name;
        private static readonly string ENDS_WITH = ReflectionHelper.GetMethod<string>(x => x.EndsWith("null")).Name;

        private static readonly IDictionary<ExpressionType, string> _operators = new Dictionary<ExpressionType, string>
        {
            {ExpressionType.Equal, "="},
            {ExpressionType.NotEqual, "!="},
            {ExpressionType.GreaterThan, ">"},
            {ExpressionType.GreaterThanOrEqual, ">="},
            {ExpressionType.LessThan, "<"},
            {ExpressionType.LessThanOrEqual, "<="}
        };

        public static string ApplyCastToLocator(this string locator, Type memberType)
        {
            if (memberType.IsEnum)
            {
                return "({0})::int".ToFormat(locator);
            }

            if (!TypeMappings.PgTypes.ContainsKey(memberType))
                throw new ArgumentOutOfRangeException(nameof(memberType),
                    "There is not a known Postgresql cast for member type " + memberType.FullName);

            return "CAST({0} as {1})".ToFormat(locator, TypeMappings.PgTypes[memberType]);
        }

        public static IWhereFragment ParseWhereFragment(DocumentMapping mapping, Expression expression)
        {
            if (expression is BinaryExpression)
            {
                return GetWhereFragment(mapping, expression.As<BinaryExpression>());
            }

            if (expression.NodeType == ExpressionType.Call)
            {
                return GetMethodCall(mapping, expression.As<MethodCallExpression>());
            }

            if (expression is MemberExpression && expression.Type == typeof(bool))
            {
                var locator = JsonLocator(mapping, expression.As<MemberExpression>());
                return new WhereFragment("({0})::Boolean = True".ToFormat(locator), true);
            }

            if (expression.NodeType == ExpressionType.Not)
            {
                return GetNotWhereFragment(mapping, expression.As<UnaryExpression>().Operand);
            }


            throw new NotSupportedException();
        }

        private static IWhereFragment GetNotWhereFragment(DocumentMapping mapping, Expression expression)
        {
            if (expression is MemberExpression && expression.Type == typeof(bool))
            {
                var locator = JsonLocator(mapping, expression.As<MemberExpression>());
                return new WhereFragment("({0})::Boolean = False".ToFormat(locator));
            }

            throw new NotSupportedException();
        }

        private static IWhereFragment GetMethodCall(DocumentMapping mapping, MethodCallExpression expression)
        {

            // TODO -- generalize this mess
            if (expression.Method.Name == CONTAINS)
            {
                var @object = expression.Object;

                if (@object.Type == typeof (string))
                {
                    var locator = JsonLocator(mapping, @object);
                    var value = Value(expression.Arguments.Single()).As<string>();
                    return new WhereFragment("{0} like ?".ToFormat(locator), "%" + value + "%");
                }
            }

            if (expression.Method.Name == STARTS_WITH)
            {
                var @object = expression.Object;
                if (@object.Type == typeof(string))
                {
                    var locator = JsonLocator(mapping, @object);
                    var value = Value(expression.Arguments.Single()).As<string>();
                    return new WhereFragment("{0} like ?".ToFormat(locator), value + "%");
                }
            }

            if (expression.Method.Name == ENDS_WITH)
            {
                var @object = expression.Object;
                if (@object.Type == typeof(string))
                {
                    var locator = JsonLocator(mapping, @object);
                    var value = Value(expression.Arguments.Single()).As<string>();
                    return new WhereFragment("{0} like ?".ToFormat(locator), "%" + value);
                }
            }

            throw new NotImplementedException();
        }

        public static IWhereFragment GetWhereFragment(DocumentMapping mapping, BinaryExpression binary)
        {
            if (_operators.ContainsKey(binary.NodeType))
            {
                return buildSimpleWhereClause(mapping, binary);
            }


            switch (binary.NodeType)
            {
                case ExpressionType.AndAlso:
                    return new CompoundWhereFragment("and", ParseWhereFragment(mapping, binary.Left),
                        ParseWhereFragment(mapping, binary.Right));

                case ExpressionType.OrElse:
                    return new CompoundWhereFragment("or", ParseWhereFragment(mapping, binary.Left),
                        ParseWhereFragment(mapping, binary.Right));
            }

            throw new NotSupportedException();
        }

        private static IWhereFragment buildSimpleWhereClause(DocumentMapping mapping, BinaryExpression binary)
        {
            var jsonLocator = JsonLocator(mapping, binary.Left);
            

            var value = Value(binary.Right);

            if (value == null)
            {
                return new WhereFragment("{0} is null".ToFormat(jsonLocator));
            }

            var op = _operators[binary.NodeType];
            return new WhereFragment("{0} {1} ?".ToFormat(jsonLocator, op), value);
        }

        public static object Value(Expression expression)
        {
            if (expression is ConstantExpression)
            {
                // TODO -- handle nulls
                // TODO -- check out more types here.
                return expression.As<ConstantExpression>().Value;
            }

            throw new NotSupportedException();
        }

        public static string JsonLocator(DocumentMapping mapping, Expression expression)
        {
            if (expression is MemberExpression)
            {
                var memberExpression = expression.As<MemberExpression>();
                return JsonLocator(mapping, memberExpression);
            }

            if (expression is UnaryExpression)
            {
                return JsonLocator(mapping, expression.As<UnaryExpression>());
            }

            throw new NotSupportedException();
        }

        public static string JsonLocator(DocumentMapping mapping, UnaryExpression expression)
        {
            if (expression.NodeType == ExpressionType.Convert)
            {
                return JsonLocator(mapping, expression.Operand);
            }

            throw new NotSupportedException();
        }

        public static string JsonLocator(DocumentMapping mapping, MemberExpression memberExpression)
        {
            var memberType = memberExpression.Member.GetMemberType();

            var path = " ->> '{0}' ".ToFormat(memberExpression.Member.Name);
            var parent = memberExpression.Expression as MemberExpression;
            while (parent != null)
            {
                path = " -> '{0}' ".ToFormat(parent.Member.Name) + path;
                parent = parent.Expression as MemberExpression;
            }


            var locator = "data{0}".ToFormat(path).TrimEnd();


            if (memberType == typeof (string)) return locator;

            return locator.ApplyCastToLocator(memberType);
        }
    }
}